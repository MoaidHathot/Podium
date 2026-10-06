using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Core.Services;

public sealed class LiveSessionOptions
{
    /// <summary>A session with no presenter connected for this long ends by itself (a forgotten tab must not hold anything).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>Grace beyond the planned length before a session is ended automatically.</summary>
    public TimeSpan OvertimeGrace { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>No session lives longer than this, planned or not.</summary>
    public TimeSpan HardCap { get; set; } = TimeSpan.FromHours(4);
}

/// <summary>
/// Live sessions: an explicit "Go live" / "End session" around a talk. Starting freezes the deck (so a push mid-talk
/// cannot swap the slides), mints a join link that dies with the session and begins recording where the presenter
/// is; ending revokes the link, optionally unfreezes and writes a pacing recap. Sessions also end on their own when
/// the presenter disappears, so the deployment guard can never be held hostage by a forgotten session.
/// </summary>
/// <summary>Process-wide pacing recorders (one per live deck); fed by the sync hub, read when a session ends.</summary>
public sealed class SessionRecorders(ISessionStore sessions, IDeckStore decks)
{
    internal sealed class Recorder
    {
        public int CurrentSlide;
        public DateTimeOffset Since;
        public int PeakViewers;
        public readonly ConcurrentDictionary<int, double> Seconds = new();
        public readonly HashSet<int> Visited = [];
        public readonly object Gate = new();
    }

    internal readonly ConcurrentDictionary<string, Recorder> Recorders = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised after a session starts, changes its plan or ends (the ended session carries its recap). The web host
    /// uses it to tell every connected deck window and remote; failures are logged by the caller and never block.
    /// </summary>
    public Func<Session, Task>? OnSessionChanged { get; set; }

    /// <summary>Called by the sync hub whenever a presenter reports a position.</summary>
    public Task RecordPositionAsync(string slug, int page, int clicks, DateTimeOffset at)
    {
        if (!Recorders.TryGetValue(slug, out var rec)) return Task.CompletedTask;
        lock (rec.Gate)
        {
            if (rec.CurrentSlide > 0 && rec.CurrentSlide != page)
                rec.Seconds.AddOrUpdate(rec.CurrentSlide, (at - rec.Since).TotalSeconds, (_, v) => v + (at - rec.Since).TotalSeconds);
            if (rec.CurrentSlide != page) { rec.CurrentSlide = page; rec.Since = at; }
            rec.Visited.Add(page);
        }
        return Task.CompletedTask;
    }

    /// <summary>Called by the sync hub on presence changes; tracks peak audience and when a presenter was last seen.</summary>
    public async Task RecordPresenceAsync(string slug, int presenters, int viewers)
    {
        if (!Recorders.TryGetValue(slug, out var rec)) return;
        lock (rec.Gate) rec.PeakViewers = Math.Max(rec.PeakViewers, viewers);
        if (presenters > 0)
        {
            var deck = await decks.GetAsync(slug);
            if (deck?.LiveSessionId is { } sid && await sessions.GetAsync(slug, sid) is { EndedAt: null } s && (DateTimeOffset.UtcNow - (s.LastPresenterSeenAt ?? s.StartedAt)) > TimeSpan.FromMinutes(1))
                await sessions.UpsertAsync(s with { LastPresenterSeenAt = DateTimeOffset.UtcNow });
        }
    }
}

public sealed class SessionService(
    ISessionStore sessions,
    IDeckStore decks,
    IShareLinkStore links,
    BuildService builds,
    SessionRecorders recorders,
    Microsoft.Extensions.Options.IOptions<LiveSessionOptions> options,
    ILogger<SessionService> log)
{
    private readonly ConcurrentDictionary<string, SessionRecorders.Recorder> _recorders = recorders.Recorders;

    public async Task<(Session? Session, string? Error)> StartAsync(string slug, int? plannedMinutes, bool holdDeploys, bool freeze, string? title, CancellationToken ct = default, AudienceSettings? audience = null, bool rehearsal = false)
    {
        var deck = await decks.GetAsync(slug, ct);
        if (deck is null || deck.Archived) return (null, "Deck not found");
        if (deck.CurrentBuildId is null) return (null, "Deck has no build to present yet");
        if (deck.LiveSessionId is not null && await sessions.GetAsync(slug, deck.LiveSessionId, ct) is { EndedAt: null })
            return (null, "A session is already live for this deck");
        if (plannedMinutes is < 1 or > 600) return (null, "Planned length must be between 1 and 600 minutes");

        var id = NewId();
        var froze = false;
        if (freeze && deck.PinnedBuildId is null)
        {
            await builds.SetFrozenAsync(slug, true, ct);
            froze = true;
        }
        var link = new ShareLink
        {
            Id = NewId(),
            DeckSlug = slug,
            Artifact = ArtifactKind.Site,
            Label = $"Live session {DateTimeOffset.UtcNow:yyyy-MM-dd}",
            ExpiresAt = DateTimeOffset.UtcNow + options.Value.HardCap,
            SessionId = id,
        };
        await links.UpsertAsync(link, ct);

        // Short code for the room: unique among live sessions; letters/digits that cannot be confused when read aloud.
        var live = await sessions.ListLiveAsync(ct);
        string code;
        do { code = NewJoinCode(); } while (live.Any(s => string.Equals(s.JoinCode, code, StringComparison.OrdinalIgnoreCase)));

        var session = new Session
        {
            Id = id,
            DeckSlug = slug,
            PlannedMinutes = plannedMinutes,
            HoldDeploys = holdDeploys,
            LinkId = link.Id,
            JoinCode = code,
            FrozeDeck = froze,
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim()[..Math.Min(120, title.Trim().Length)],
            LastPresenterSeenAt = DateTimeOffset.UtcNow,
            Audience = audience ?? deck.Audience,
            Rehearsal = rehearsal,
        };
        await sessions.UpsertAsync(session, ct);
        var latest = await decks.GetAsync(slug, ct) ?? deck;
        await decks.UpsertAsync(latest with { LiveSessionId = id, HoldDeploysWhileLive = holdDeploys, UpdatedAt = DateTimeOffset.UtcNow }, ct);
        _recorders[slug] = new SessionRecorders.Recorder { Since = DateTimeOffset.UtcNow };
        log.LogInformation("Session {Session} started for {Deck} (planned {Planned} min, hold deploys {Hold})", id, slug, plannedMinutes, holdDeploys);
        await NotifyAsync(session);
        return (session, null);
    }

    public async Task<Session?> EndAsync(string slug, string reason, bool unfreeze, CancellationToken ct = default)
    {
        var deck = await decks.GetAsync(slug, ct);
        if (deck?.LiveSessionId is null) return null;
        var session = await sessions.GetAsync(slug, deck.LiveSessionId, ct);
        if (session is null || session.EndedAt is not null)
        {
            await decks.UpsertAsync(deck with { LiveSessionId = null }, ct);
            return session;
        }
        var now = DateTimeOffset.UtcNow;
        _recorders.TryRemove(slug, out var rec);
        var recap = BuildRecap(session, rec, now);
        session = session with { EndedAt = now, EndReason = reason, Recap = recap };
        await sessions.UpsertAsync(session, ct);

        if (session.LinkId is not null && await links.GetAsync(session.LinkId, ct) is { } link && !link.Revoked)
            await links.UpsertAsync(link with { Revoked = true }, ct);
        if (unfreeze && session.FrozeDeck) await builds.SetFrozenAsync(slug, false, ct);
        var latest = await decks.GetAsync(slug, ct) ?? deck;
        await decks.UpsertAsync(latest with { LiveSessionId = null, UpdatedAt = now }, ct);
        log.LogInformation("Session {Session} for {Deck} ended ({Reason}): {Duration}s, peak {Peak} viewers", session.Id, slug, reason, recap.DurationSeconds, recap.PeakViewers);
        await NotifyAsync(session);
        return session;
    }

    private async Task NotifyAsync(Session session)
    {
        if (recorders.OnSessionChanged is not { } notify) return;
        try { await notify(session); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Session change notification failed for {Deck}", session.DeckSlug); }
    }

    /// <summary>
    /// Maintenance tick: ends sessions whose presenter has been gone for <see cref="SessionOptions.IdleTimeout"/>,
    /// that overran their plan by more than the grace, or that hit the hard cap. Returns the number ended.
    /// </summary>
    public async Task<int> SweepAsync(Func<string, int> presentersConnected, CancellationToken ct = default)
    {
        var ended = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var s in await sessions.ListLiveAsync(ct))
        {
            var presenters = presentersConnected(s.DeckSlug);
            var lastSeen = s.LastPresenterSeenAt ?? s.StartedAt;
            if (presenters > 0 && now - lastSeen > TimeSpan.FromMinutes(1))
            {
                await sessions.UpsertAsync(s with { LastPresenterSeenAt = now }, ct);
                lastSeen = now;
            }
            string? reason = null;
            if (now - s.StartedAt > options.Value.HardCap) reason = "cap";
            else if (s.PlannedMinutes is { } planned && now - s.StartedAt > TimeSpan.FromMinutes(planned) + options.Value.OvertimeGrace) reason = "overtime";
            else if (presenters == 0 && now - lastSeen > options.Value.IdleTimeout) reason = "idle";
            if (reason is null) continue;
            // Ensure the recorder exists even after a restart so the recap has at least duration/peak.
            _recorders.TryAdd(s.DeckSlug, new SessionRecorders.Recorder { Since = s.StartedAt });
            if (await EndAsync(s.DeckSlug, reason, unfreeze: false, ct) is not null) ended++;
        }
        return ended;
    }

    /// <summary>Resolves a join code typed by the room to its live session (case-insensitive, dashes ignored).</summary>
    public async Task<Session?> FindLiveByJoinCodeAsync(string code, CancellationToken ct = default)
    {
        var normalized = NormalizeJoinCode(code);
        if (normalized is null) return null;
        return (await sessions.ListLiveAsync(ct)).FirstOrDefault(s => s.JoinCode is not null && string.Equals(s.JoinCode, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The live session of a deck, or null.</summary>
    public async Task<Session?> GetLiveAsync(string slug, CancellationToken ct = default)
    {
        var deck = await decks.GetAsync(slug, ct);
        if (deck?.LiveSessionId is null) return null;
        var s = await sessions.GetAsync(slug, deck.LiveSessionId, ct);
        return s is { EndedAt: null } ? s : null;
    }

    /// <summary>Changes the planned length of a live session (the organiser just cut five minutes...).</summary>
    public async Task<Session?> UpdatePlanAsync(string slug, int? plannedMinutes, CancellationToken ct = default)
    {
        if (plannedMinutes is < 1 or > 600) return null;
        var s = await GetLiveAsync(slug, ct);
        if (s is null) return null;
        s = s with { PlannedMinutes = plannedMinutes };
        await sessions.UpsertAsync(s, ct);
        await NotifyAsync(s);
        return s;
    }

    /// <summary>Mutes or unmutes the room and/or changes which audience features are offered, mid-session.</summary>
    public async Task<Session?> UpdateAudienceAsync(string slug, bool? muted, AudienceSettings? settings, CancellationToken ct = default)
    {
        var s = await GetLiveAsync(slug, ct);
        if (s is null) return null;
        s = s with { AudienceMuted = muted ?? s.AudienceMuted, Audience = settings ?? s.Audience };
        await sessions.UpsertAsync(s, ct);
        await NotifyAsync(s);
        return s;
    }

    /// <summary>Public join URL for a session's code (short enough to read aloud and to make a sparse QR).</summary>
    public static string JoinUrl(Uri publicBaseUrl, string joinCode) => $"{publicBaseUrl.ToString().TrimEnd('/')}/j/{FormatJoinCode(joinCode)}";

    /// <summary>"ABC123" shown as "ABC-123".</summary>
    public static string FormatJoinCode(string code) => code.Length == 6 ? $"{code[..3]}-{code[3..]}" : code;

    public static string? NormalizeJoinCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var clean = new string(code.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        return clean.Length == 6 && clean.All(JoinAlphabet.Contains) ? clean : null;
    }

    // No I/L/O/0/1: unambiguous on a projector and over the phone. 31^6 = 887 million codes.
    private const string JoinAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private static string NewJoinCode()
        => string.Create(6, 0, (span, _) => { for (var i = 0; i < span.Length; i++) span[i] = JoinAlphabet[RandomNumberGenerator.GetInt32(JoinAlphabet.Length)]; });

    /// <summary>Live sessions that asked to hold deployments; used by the deploy guard endpoint.</summary>
    public async Task<IReadOnlyList<Session>> HoldingDeploysAsync(CancellationToken ct = default)
        => (await sessions.ListLiveAsync(ct)).Where(s => s.HoldDeploys).ToList();

    private static SessionRecap BuildRecap(Session session, SessionRecorders.Recorder? rec, DateTimeOffset now)
    {
        var duration = (int)Math.Max(0, (now - session.StartedAt).TotalSeconds);
        if (rec is null) return new SessionRecap(duration, 0, 0, new Dictionary<int, int>(), null);
        lock (rec.Gate)
        {
            if (rec.CurrentSlide > 0)
                rec.Seconds.AddOrUpdate(rec.CurrentSlide, (now - rec.Since).TotalSeconds, (_, v) => v + (now - rec.Since).TotalSeconds);
            var perSlide = rec.Seconds.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => (int)Math.Round(kv.Value));
            return new SessionRecap(duration, rec.PeakViewers, rec.Visited.Count, perSlide, rec.CurrentSlide > 0 ? rec.CurrentSlide : null);
        }
    }

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
