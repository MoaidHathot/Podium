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
}

/// <summary>Report posted by the builder when it finishes.</summary>
public sealed record BuildReport(bool Success, bool HasSite, bool HasPdf, bool HasPptx, string? Error, IReadOnlyList<string>? Warnings);

public sealed class BuildService(
    IBuildStore builds,
    IDeckStore decks,
    IArtifactStore artifacts,
    IRepositoryClient repos,
    IBuildRunner runner,
    IBuildTokenService tokens,
    IOptions<BuildOptions> options,
    ILogger<BuildService> log)
{
    public async Task<Build> QueueAsync(Deck deck, Source source, string sha, string triggeredBy, IReadOnlyList<string> warnings, CancellationToken ct = default)
    {
        // Collapse duplicates: an active build for the same deck+sha is reused.
        var active = await builds.ListActiveAsync(ct);
        var dup = active.FirstOrDefault(b => b.DeckSlug == deck.Slug && b.Sha == sha);
        if (dup is not null) return dup;

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
            Error = report.Success && report.HasSite ? null : (report.Error ?? "Builder reported failure"),
            Warnings = warnings,
        };
        await builds.UpsertAsync(build, ct);

        var deck = await decks.GetAsync(deckSlug, ct);
        if (deck is not null)
        {
            var previous = deck.CurrentBuildId;
            deck = deck with
            {
                LatestBuildStatus = build.Status,
                CurrentBuildId = build.Status == BuildStatus.Succeeded ? build.Id : deck.CurrentBuildId,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await decks.UpsertAsync(deck, ct);

            if (build.Status == BuildStatus.Succeeded && previous is not null && previous != build.Id)
            {
                // Keep exactly one older build for quick rollback; delete anything before that.
                var history = await builds.ListForDeckAsync(deckSlug, 50, ct);
                foreach (var old in history.Where(b => b.Status == BuildStatus.Succeeded && b.Id != build.Id && b.Id != previous))
                {
                    try { await artifacts.DeleteBuildAsync(deckSlug, old.Id, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Cleanup of build {Build} failed", old.Id); }
                }
            }
        }

        log.LogInformation("Build {Build} for {Deck} finished: {Status} {Error}", buildId, deckSlug, build.Status, build.Error);
        return true;
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
        foreach (var deck in allDecks.Where(d => !d.Archived && d.Kind is DeckKind.Slidev or DeckKind.Presenterm or DeckKind.Static))
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

    private static string NewBuildId()
    {
        // Time-sortable, URL-safe: yyyyMMddHHmmss + 6 random base32 chars.
        var ts = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        Span<byte> rnd = stackalloc byte[6];
        RandomNumberGenerator.Fill(rnd);
        var tail = string.Create(6, rnd.ToArray(), (span, bytes) => { for (var i = 0; i < span.Length; i++) span[i] = alphabet[bytes[i] % 32]; });
        return ts + tail;
    }
}

/// <summary>Issues and validates HMAC tokens that authorise a builder to report exactly one build.</summary>
public interface IBuildTokenService
{
    string Issue(string deckSlug, string buildId, TimeSpan lifetime);
    bool Validate(string token, string deckSlug, string buildId);
}
