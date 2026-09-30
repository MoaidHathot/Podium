using Microsoft.Extensions.Logging;
using Podium.Core.Abstractions;
using Podium.Core.Discovery;
using Podium.Core.Models;

namespace Podium.Core.Services;

/// <summary>
/// Reconciles a source's repository tree with the deck index and queues builds for changed decks.
/// </summary>
public sealed class DeckSyncService(
    ISourceStore sources,
    IDeckStore decks,
    IRepositoryClient repos,
    BuildService builds,
    ILogger<DeckSyncService> log)
{
    /// <summary>Scans a source. When <paramref name="changedPaths"/> is given (webhook), only decks touching those paths are rebuilt.</summary>
    public async Task<SyncResult> SyncAsync(Source source, IReadOnlyCollection<string>? changedPaths = null, bool forceRebuild = false, string? triggeredBy = null, CancellationToken ct = default)
    {
        var (sha, committedAt) = await repos.GetHeadAsync(source, ct);
        var tree = await repos.ListTreeAsync(source, sha, ct);
        var candidates = DeckDetector.Detect(tree);
        var existing = (await decks.ListBySourceAsync(source.Id, ct)).ToDictionary(d => d.Path, StringComparer.Ordinal);
        var allDecks = await decks.ListAsync(includeArchived: true, ct);
        var takenSlugs = allDecks.Select(d => d.Slug).ToHashSet(StringComparer.Ordinal);

        // Decide which paths changed since the last scan:
        //   null  => everything is considered changed (first scan, forced, or diff unavailable)
        //   empty => nothing changed
        IReadOnlyCollection<string>? effectiveChanged = changedPaths;
        if (effectiveChanged is null && !forceRebuild && source.LastSeenSha is not null)
        {
            if (source.LastSeenSha == sha)
            {
                effectiveChanged = [];
            }
            else
            {
                try { effectiveChanged = await repos.DiffPathsAsync(source, source.LastSeenSha, sha, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Diff {From}..{To} failed for {Source}; rebuilding all decks", source.LastSeenSha, sha, source.Id);
                    effectiveChanged = null;
                }
            }
        }

        var result = new SyncResult();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in candidates)
        {
            seenPaths.Add(c.Path);
            existing.TryGetValue(c.Path, out var prev);

            var touched = prev is null || forceRebuild || effectiveChanged is null || effectiveChanged.Any(p => PathTouchesDeck(p, c.Path));
            var metadata = touched ? await ReadMetadataAsync(source, sha, c, ct) : null;

            var slug = prev?.Slug ?? UniqueSlug(Slug.ForDeck(source.Repo, c.Path), takenSlugs);
            takenSlugs.Add(slug);

            var last = touched ? await repos.LastCommitForPathAsync(source, sha, c.Path, ct) : null;

            var deck = (prev ?? new Deck
            {
                Slug = slug,
                SourceId = source.Id,
                Path = c.Path,
                Entry = c.Entry,
                Kind = c.Kind,
                Visibility = Visibility.Private,
                ExportPdf = c.Kind is DeckKind.Slidev or DeckKind.Presenterm,
            }) with
            {
                Entry = c.Entry,
                Kind = c.Kind,
                Archived = false,
                Title = metadata?.Title ?? prev?.Title ?? Humanize(c.Path, source.Repo),
                Author = metadata?.Author ?? prev?.Author,
                Description = metadata?.Description ?? prev?.Description,
                Tags = metadata?.Tags ?? prev?.Tags ?? [],
                LastCommitSha = last?.Sha ?? prev?.LastCommitSha ?? sha,
                LastCommitAt = last?.CommittedAt ?? prev?.LastCommitAt ?? committedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            await decks.UpsertAsync(deck, ct);
            if (prev is null) result.Added.Add(deck.Slug);

            var buildable = c.Kind is DeckKind.Slidev or DeckKind.Presenterm or DeckKind.Static;
            // Rebuild when content changed, or when the deck has never been attempted. Failed builds are not
            // retried automatically (use the Rebuild action) to avoid looping on permanently broken decks.
            var needsBuild = buildable && (forceRebuild || touched || deck.LatestBuildId is null);
            if (needsBuild)
            {
                await builds.QueueAsync(deck, source, sha, triggeredBy ?? "sync", metadata?.UnsupportedFeatures ?? [], ct);
                result.Queued.Add(deck.Slug);
            }
        }

        foreach (var (path, deck) in existing)
        {
            if (!seenPaths.Contains(path) && !deck.Archived)
            {
                await decks.UpsertAsync(deck with { Archived = true, UpdatedAt = DateTimeOffset.UtcNow }, ct);
                result.Archived.Add(deck.Slug);
            }
        }

        await sources.UpsertAsync(source with { LastSeenSha = sha, LastScannedAt = DateTimeOffset.UtcNow }, ct);
        log.LogInformation("Synced {Source}@{Sha}: +{Added} ~{Queued} -{Archived}", source.Id, sha[..7], result.Added.Count, result.Queued.Count, result.Archived.Count);
        return result;
    }

    private async Task<DeckMetadata?> ReadMetadataAsync(Source source, string sha, DeckCandidate c, CancellationToken ct)
    {
        try
        {
            var path = string.IsNullOrEmpty(c.Path) ? c.Entry : $"{c.Path}/{c.Entry}";
            return c.Kind switch
            {
                DeckKind.Slidev => DeckMetadataReader.ReadSlidev(await repos.ReadTextFileAsync(source, sha, path, ct) ?? ""),
                DeckKind.Presenterm => DeckMetadataReader.ReadPresenterm(await repos.ReadTextFileAsync(source, sha, path, ct) ?? ""),
                DeckKind.GitPitch => DeckMetadataReader.ReadSlidev(await repos.ReadTextFileAsync(source, sha, path, ct) ?? ""),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Failed reading metadata for {Path}", c.Path);
            return null;
        }
    }

    private static bool PathTouchesDeck(string changed, string deckPath)
    {
        changed = changed.Replace('\\', '/');
        if (string.IsNullOrEmpty(deckPath)) return !changed.Contains('/');
        return changed.StartsWith(deckPath + "/", StringComparison.Ordinal);
    }

    private static string UniqueSlug(string baseSlug, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(baseSlug)) return baseSlug;
        for (var i = 2; ; i++)
        {
            var s = $"{baseSlug}-{i}";
            if (!taken.Contains(s)) return s;
        }
    }

    private static string Humanize(string path, string repo)
    {
        var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? repo;
        return last.Replace('-', ' ').Replace('_', ' ');
    }
}

public sealed class SyncResult
{
    public List<string> Added { get; } = [];
    public List<string> Queued { get; } = [];
    public List<string> Archived { get; } = [];
}
