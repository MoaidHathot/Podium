using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Core.Services;

public sealed class BuildOptions
{
    /// <summary>Public base URL of the service.</summary>
    public required Uri PublicBaseUrl { get; set; }
    /// <summary>Base URL the builder uses to report back; defaults to <see cref="PublicBaseUrl"/>. Point it at the platform FQDN so callbacks never depend on custom-domain state.</summary>
    public Uri? CallbackBaseUrl { get; set; }
    public TimeSpan TrustedTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan UntrustedTimeout { get; set; } = TimeSpan.FromMinutes(8);
    /// <summary>Builds older than this that still report Running are marked failed.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>Build records kept per deck (artifacts are retained only for the served, pinned and rollback builds).</summary>
    public int KeepBuildRecords { get; set; } = 25;
    /// <summary>Archived decks (removed from the repository) lose their artifacts after this long.</summary>
    public TimeSpan ArchivedPurgeAfter { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>Report posted by the builder when it finishes.</summary>
public sealed record BuildReport(bool Success, bool HasSite, bool HasPdf, bool HasPptx, string? Error, IReadOnlyList<string>? Warnings, bool HasThumbnail = false, bool HasPublicSite = false);

public sealed class BuildService(
    IBuildStore builds,
    IDeckStore decks,
    IArtifactStore artifacts,
    IRepositoryClient repos,
    IBuildRunner runner,
    IBuildTokenService tokens,
    IOptions<BuildOptions> options,
    ILogger<BuildService> log,
    ISourceStore? sources = null,
    IEnumerable<IBuildObserver>? observers = null)
{
    private readonly IReadOnlyList<IBuildObserver> _observers = observers?.ToList() ?? [];
    public async Task<Build> QueueAsync(Deck deck, Source source, string sha, string triggeredBy, IReadOnlyList<string> warnings, CancellationToken ct = default, bool supersedeActive = false)
    {
        // Collapse duplicates: an active build for the same deck+sha is reused, unless the caller explicitly wants a
        // fresh run (manual rebuild), in which case the stale one is cancelled so it cannot report over the new build.
        var active = await builds.ListActiveAsync(ct);
        var dup = active.FirstOrDefault(b => b.DeckSlug == deck.Slug && b.Sha == sha);
        if (dup is not null)
        {
            if (!supersedeActive) return dup;
            await builds.UpsertAsync(dup with { Status = BuildStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow, Error = "Superseded by a manual rebuild" }, ct);
            log.LogInformation("Cancelled active build {Build} for {Deck} (superseded)", dup.Id, deck.Slug);
        }

        var build = new Build
        {
            Id = NewBuildId(),
            DeckSlug = deck.Slug,
            Sha = sha,
            Status = BuildStatus.Queued,
            TriggeredBy = triggeredBy,
            Warnings = warnings,
            BuilderVersion = await SafeBuilderVersionAsync(ct),
        };
        await builds.UpsertAsync(build, ct);
        await decks.UpsertAsync(deck with { LatestBuildId = build.Id, LatestBuildStatus = BuildStatus.Queued, UpdatedAt = DateTimeOffset.UtcNow }, ct);

        try
        {
            var timeout = source.Trusted ? options.Value.TrustedTimeout : options.Value.UntrustedTimeout;
            var cloneUrl = await repos.GetAuthenticatedCloneUrlAsync(source, ct);
            var upload = await artifacts.CreateUploadUriAsync(deck.Slug, build.Id, timeout + TimeSpan.FromMinutes(5), ct);
            var callbackToken = tokens.Issue(deck.Slug, build.Id, timeout + TimeSpan.FromMinutes(10));
            var callback = new Uri(options.Value.CallbackBaseUrl ?? options.Value.PublicBaseUrl, $"/api/builds/{Uri.EscapeDataString(deck.Slug)}/{build.Id}/report");

            var execId = await runner.StartAsync(new BuildRequest(build, deck, source, cloneUrl, upload, callback, callbackToken, timeout), ct);

            // The builder may (in theory) have reported completion already; never regress a terminal status.
            var current = await builds.GetAsync(deck.Slug, build.Id, ct) ?? build;
            if (current.Status == BuildStatus.Queued)
            {
                build = current with { Status = BuildStatus.Running, StartedAt = DateTimeOffset.UtcNow, RunnerExecutionId = execId };
                await builds.UpsertAsync(build, ct);
                var d = await decks.GetAsync(deck.Slug, ct) ?? deck;
                if (d.LatestBuildId == build.Id)
                    await decks.UpsertAsync(d with { LatestBuildStatus = BuildStatus.Running }, ct);
            }
            else
            {
                build = current with { RunnerExecutionId = execId };
                await builds.UpsertAsync(build, ct);
            }
            log.LogInformation("Build {Build} for {Deck}@{Sha} started as {Exec}", build.Id, deck.Slug, sha[..7], execId);

            foreach (var observer in _observers)
            {
                try
                {
                    var reference = await observer.OnStartedAsync(build, deck, source, ct);
                    if (reference is not null)
                    {
                        var latest = await builds.GetAsync(deck.Slug, build.Id, ct) ?? build;
                        build = latest with { ExternalRef = reference };
                        await builds.UpsertAsync(build, ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Build observer {Observer} failed on start", observer.GetType().Name); }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Failed to start build {Build} for {Deck}", build.Id, deck.Slug);
            build = build with { Status = BuildStatus.Failed, FinishedAt = DateTimeOffset.UtcNow, Error = "Failed to start: " + ex.Message };
            await builds.UpsertAsync(build, ct);
            var d = await decks.GetAsync(deck.Slug, ct) ?? deck;
            if (d.LatestBuildId == build.Id)
                await decks.UpsertAsync(d with { LatestBuildStatus = BuildStatus.Failed }, ct);
        }
        return build;
    }

    public async Task<bool> CompleteAsync(string deckSlug, string buildId, string token, BuildReport report, CancellationToken ct = default)
    {
        if (!tokens.Validate(token, deckSlug, buildId)) return false;
        var build = await builds.GetAsync(deckSlug, buildId, ct);
        if (build is null || build.Status is BuildStatus.Succeeded or BuildStatus.Failed or BuildStatus.Cancelled) return false;

        var warnings = build.Warnings.Concat(report.Warnings ?? []).Distinct(StringComparer.Ordinal).ToList();
        build = build with
        {
            Status = report.Success && report.HasSite ? BuildStatus.Succeeded : BuildStatus.Failed,
            FinishedAt = DateTimeOffset.UtcNow,
            HasSite = report.HasSite,
            HasPdf = report.HasPdf,
            HasPptx = report.HasPptx,
            HasThumbnail = report.HasThumbnail,
            HasPublicSite = report.HasPublicSite,
            Error = report.Success && report.HasSite ? null : (report.Error ?? "Builder reported failure"),
            Warnings = warnings,
        };
        await builds.UpsertAsync(build, ct);

        var deck = await decks.GetAsync(deckSlug, ct);
        if (deck is not null)
        {
            var succeeded = build.Status == BuildStatus.Succeeded;
            // A frozen deck keeps serving its pinned build; the new build is recorded as the latest success only.
            var serveIt = succeeded && deck.PinnedBuildId is null;
            deck = deck with
            {
                LatestBuildStatus = build.Status,
                LatestSuccessfulBuildId = succeeded ? build.Id : deck.LatestSuccessfulBuildId,
                CurrentBuildId = serveIt ? build.Id : deck.CurrentBuildId,
                CurrentHasPdf = serveIt ? build.HasPdf : deck.CurrentHasPdf,
                CurrentHasPptx = serveIt ? build.HasPptx : deck.CurrentHasPptx,
                CurrentHasThumbnail = serveIt ? build.HasThumbnail : deck.CurrentHasThumbnail,
                CurrentHasPublicSite = serveIt ? build.HasPublicSite : deck.CurrentHasPublicSite,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await decks.UpsertAsync(deck, ct);

            await ApplyRetentionAsync(deck, ct);
        }

        log.LogInformation("Build {Build} for {Deck} finished: {Status} {Error}", buildId, deckSlug, build.Status, build.Error);

        if (_observers.Count > 0 && deck is not null && sources is not null && await sources.GetAsync(deck.SourceId, ct) is { } src)
        {
            foreach (var observer in _observers)
            {
                try { await observer.OnFinishedAsync(build, deck, src, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Build observer {Observer} failed on finish", observer.GetType().Name); }
            }
        }
        return true;
    }

    /// <summary>
    /// Retention: artifacts are kept for the served build, the pinned build (if any) and the most recent other successful
    /// build (rollback target); everything else loses its blobs. Build records are capped at <see cref="BuildOptions.KeepBuildRecords"/>
    /// so history stays browsable without growing forever. Never touches active builds.
    /// </summary>
    public async Task ApplyRetentionAsync(Deck deck, CancellationToken ct = default)
    {
        var history = await builds.ListForDeckAsync(deck.Slug, 200, ct); // newest first
        var keepArtifacts = new HashSet<string>(StringComparer.Ordinal);
        if (deck.CurrentBuildId is not null) keepArtifacts.Add(deck.CurrentBuildId);
        if (deck.PinnedBuildId is not null) keepArtifacts.Add(deck.PinnedBuildId);
        var rollback = history.FirstOrDefault(b => b.Status == BuildStatus.Succeeded && !keepArtifacts.Contains(b.Id));
        if (rollback is not null) keepArtifacts.Add(rollback.Id);

        var index = 0;
        foreach (var b in history)
        {
            index++;
            if (b.Status is BuildStatus.Queued or BuildStatus.Running) continue;
            var keepRecord = index <= options.Value.KeepBuildRecords || keepArtifacts.Contains(b.Id);
            if (b.Status == BuildStatus.Succeeded && !keepArtifacts.Contains(b.Id) && (b.HasSite || b.HasPdf || b.HasPptx))
            {
                try
                {
                    await artifacts.DeleteBuildAsync(deck.Slug, b.Id, ct);
                    // Record that the artifacts are gone so the UI does not offer to serve this build.
                    await builds.UpsertAsync(b with { HasSite = false, HasPdf = false, HasPptx = false, HasThumbnail = false }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Cleanup of build {Build} failed", b.Id); }
            }
            if (!keepRecord)
            {
                try { await builds.DeleteAsync(deck.Slug, b.Id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Deleting build record {Build} failed", b.Id); }
            }
        }
    }

    /// <summary>
    /// Serves a specific successful build (rollback or promote). With <paramref name="freeze"/> the deck stays on that
    /// build until unfrozen; otherwise newer successful builds resume replacing it.
    /// </summary>
    public async Task<Deck?> ServeBuildAsync(string deckSlug, string buildId, bool freeze, CancellationToken ct = default)
    {
        var deck = await decks.GetAsync(deckSlug, ct);
        var build = await builds.GetAsync(deckSlug, buildId, ct);
        if (deck is null || build is null || build.Status != BuildStatus.Succeeded || !build.HasSite) return null;
        deck = deck with
        {
            CurrentBuildId = build.Id,
            CurrentHasPdf = build.HasPdf,
            CurrentHasPptx = build.HasPptx,
            CurrentHasThumbnail = build.HasThumbnail,
            CurrentHasPublicSite = build.HasPublicSite,
            PinnedBuildId = freeze ? build.Id : null,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await decks.UpsertAsync(deck, ct);
        log.LogInformation("Deck {Deck} now serves build {Build} (frozen: {Frozen})", deckSlug, buildId, freeze);
        return deck;
    }

    /// <summary>Freezes on the currently served build, or unfreezes (and catches up to the latest success).</summary>
    public async Task<Deck?> SetFrozenAsync(string deckSlug, bool frozen, CancellationToken ct = default)
    {
        var deck = await decks.GetAsync(deckSlug, ct);
        if (deck is null) return null;
        if (frozen)
        {
            if (deck.CurrentBuildId is null) return deck;
            deck = deck with { PinnedBuildId = deck.CurrentBuildId, UpdatedAt = DateTimeOffset.UtcNow };
            await decks.UpsertAsync(deck, ct);
            return deck;
        }
        deck = deck with { PinnedBuildId = null, UpdatedAt = DateTimeOffset.UtcNow };
        await decks.UpsertAsync(deck, ct);
        if (deck.LatestSuccessfulBuildId is { } latest && latest != deck.CurrentBuildId)
            return await ServeBuildAsync(deckSlug, latest, freeze: false, ct) ?? deck;
        return deck;
    }
    /// <summary>Removes all artifacts and build records of a deck (archived long enough, or deleted).</summary>
    public async Task PurgeDeckAsync(Deck deck, CancellationToken ct = default)
    {
        foreach (var b in await builds.ListForDeckAsync(deck.Slug, 500, ct))
        {
            try { await artifacts.DeleteBuildAsync(deck.Slug, b.Id, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Purge of build {Build} failed", b.Id); }
            try { await builds.DeleteAsync(deck.Slug, b.Id, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Purge of build record {Build} failed", b.Id); }
        }
        log.LogInformation("Purged artifacts and build history of {Deck}", deck.Slug);
    }

    private async Task<string?> SafeBuilderVersionAsync(CancellationToken ct)
    {
        try { return await runner.GetBuilderVersionAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not determine builder version");
            return null;
        }
    }

    /// <summary>
    /// Queues rebuilds for decks whose current (or last failed) build came from a different builder version, so builder
    /// upgrades roll out without manual action. Returns the number of builds queued.
    /// </summary>
    public async Task<int> RebuildOutdatedAsync(IReadOnlyList<Deck> allDecks, Func<string, Task<Source?>> getSource, CancellationToken ct = default)
    {
        var current = await SafeBuilderVersionAsync(ct);
        if (current is null) return 0;
        var active = (await builds.ListActiveAsync(ct)).Select(b => b.DeckSlug).ToHashSet(StringComparer.Ordinal);
        var queued = 0;
        foreach (var deck in allDecks.Where(d => !d.Archived && d.Kind is DeckKind.Slidev or DeckKind.Presenterm or DeckKind.Static or DeckKind.PowerPoint or DeckKind.Pdf))
        {
            if (active.Contains(deck.Slug)) continue;
            var referenceId = deck.CurrentBuildId ?? deck.LatestBuildId;
            if (referenceId is null) continue;
            var reference = await builds.GetAsync(deck.Slug, referenceId, ct);
            if (reference is null || string.Equals(reference.BuilderVersion, current, StringComparison.Ordinal)) continue;
            var source = await getSource(deck.SourceId);
            if (source is null) continue;
            var sha = deck.LastCommitSha ?? source.LastSeenSha;
            if (sha is null) continue;
            await QueueAsync(deck, source, sha, "builder-upgrade", [], ct);
            queued++;
        }
        if (queued > 0) log.LogInformation("Queued {Count} rebuild(s) after builder change to {Version}", queued, current);
        return queued;
    }

    /// <summary>Marks runs that never reported back as failed.</summary>
    public async Task ReapStaleAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - options.Value.StaleAfter;
        foreach (var b in await builds.ListActiveAsync(ct))
        {
            var started = b.StartedAt ?? b.QueuedAt;
            if (started > cutoff) continue;
            var failed = b with { Status = BuildStatus.Failed, FinishedAt = DateTimeOffset.UtcNow, Error = "Timed out waiting for the builder to report" };
            await builds.UpsertAsync(failed, ct);
            var deck = await decks.GetAsync(b.DeckSlug, ct);
            if (deck is not null && deck.LatestBuildId == b.Id)
                await decks.UpsertAsync(deck with { LatestBuildStatus = BuildStatus.Failed }, ct);
            log.LogWarning("Reaped stale build {Build} for {Deck}", b.Id, b.DeckSlug);
        }
    }

    private static long _lastIdTicks;

    private static string NewBuildId()
    {
        // Time-sortable, URL-safe: yyyyMMddHHmmss + 3 chars of sub-second sequence + 3 random base32 chars.
        // Ids created in the same second sort in creation order (the retention logic relies on "newest first").
        var now = DateTimeOffset.UtcNow;
        long ticks;
        while (true)
        {
            var last = Interlocked.Read(ref _lastIdTicks);
            ticks = Math.Max(now.UtcTicks, last + 1);
            if (Interlocked.CompareExchange(ref _lastIdTicks, ticks, last) == last) break;
        }
        var stamped = new DateTimeOffset(ticks, TimeSpan.Zero);
        var ts = stamped.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        // Ordinal-sorted alphabet (digits before letters in ASCII) so string comparison of ids follows creation order.
        const string alphabet = "0123456789abcdefghijklmnopqrstuv";
        // 5 base32 chars encode the tick within the second exactly (10,000,000 < 32^5), so ordering is preserved; the
        // last char is random to separate ids minted on different machines in the same tick.
        var sub = ticks % TimeSpan.TicksPerSecond;
        Span<byte> rnd = stackalloc byte[1];
        RandomNumberGenerator.Fill(rnd);
        var tail = string.Create(6, (sub, rnd[0]), (span, s) =>
        {
            var v = s.sub;
            for (var i = 4; i >= 0; i--) { span[i] = alphabet[(int)(v % 32)]; v /= 32; }
            span[5] = alphabet[s.Item2 % 32];
        });
        return ts + tail;
    }
}

/// <summary>Issues and validates HMAC tokens that authorise a builder to report exactly one build.</summary>
public interface IBuildTokenService
{
    string Issue(string deckSlug, string buildId, TimeSpan lifetime);
    bool Validate(string token, string deckSlug, string buildId);
}
