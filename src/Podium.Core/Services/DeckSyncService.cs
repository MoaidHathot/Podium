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
        var treeSet = tree.Select(p => p.Replace('\\', '/').TrimStart('/')).ToHashSet(StringComparer.Ordinal);
        var candidates = DeckDetector.Detect(tree);
        // Directory-based decks are keyed by their directory; file-based ones (PowerPoint, PDF) by their full entry path.
        var existing = (await decks.ListBySourceAsync(source.Id, ct)).ToDictionary(d => DeckKey(d.Kind, d.Path, d.Entry), StringComparer.Ordinal);
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
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        // Aliases live in the slug namespace. Reserve every existing alias and assign all slugs up front, so an alias
        // declared by one deck can never collide with a slug minted for another deck later in the same pass.
        foreach (var a in allDecks.Select(d => d.Alias).Where(a => a is not null)) takenSlugs.Add(a!);
        var slugs = new Dictionary<DeckCandidate, string>();
        foreach (var c in candidates)
        {
            existing.TryGetValue(DeckKey(c.Kind, c.Path, c.Entry), out var prevForSlug);
            var s = prevForSlug?.Slug ?? UniqueSlug(DeckDetector.IsFileBased(c.Kind) ? Slug.ForFile(source.Repo, c.Entry) : Slug.ForDeck(source.Repo, c.Path), takenSlugs);
            takenSlugs.Add(s);
            slugs[c] = s;
        }

        foreach (var c in candidates)
        {
            var key = DeckKey(c.Kind, c.Path, c.Entry);
            seenKeys.Add(key);
            existing.TryGetValue(key, out var prev);
            var fileBased = DeckDetector.IsFileBased(c.Kind);

            var touched = prev is null || forceRebuild || effectiveChanged is null
                || effectiveChanged.Any(p => fileBased ? PathIsFile(p, c.EntryPath) : PathTouchesDeck(p, c.Path));
            var metadata = touched ? await ReadMetadataAsync(source, sha, c, ct) : null;

            var slug = slugs[c];

            var last = touched ? await repos.LastCommitForPathAsync(source, sha, fileBased ? c.EntryPath : c.Path, ct) : null;

            // Repository-managed settings (.podium.yml next to the deck); honoured for trusted sources only.
            var config = touched && source.Trusted && !fileBased ? await ReadConfigAsync(source, sha, c, treeSet, ct) : null;

            var deck = (prev ?? new Deck
            {
                Slug = slug,
                SourceId = source.Id,
                Path = c.Path,
                Entry = c.Entry,
                Kind = c.Kind,
                Visibility = config?.Visibility ?? Visibility.Private, // seeds new decks only; the UI wins afterwards
                ExportPdf = c.Kind is DeckKind.Slidev or DeckKind.Presenterm or DeckKind.PowerPoint or DeckKind.Pdf,
            }) with
            {
                Entry = c.Entry,
                Kind = c.Kind,
                Archived = false,
                Title = config?.Title ?? metadata?.Title ?? prev?.Title ?? (fileBased ? HumanizeFile(c.Entry) : Humanize(c.Path, source.Repo)),
                Author = metadata?.Author ?? prev?.Author,
                Description = metadata?.Description ?? prev?.Description,
                Tags = config?.Tags ?? metadata?.Tags ?? prev?.Tags ?? [],
                ExportPdf = config?.ExportPdf ?? prev?.ExportPdf ?? (c.Kind is DeckKind.Slidev or DeckKind.Presenterm or DeckKind.PowerPoint or DeckKind.Pdf),
                ExportPptx = config?.ExportPptx ?? prev?.ExportPptx ?? false,
                StripNotesForViewers = config?.StripNotes ?? prev?.StripNotesForViewers ?? true,
                Audience = config?.Audience ?? prev?.Audience ?? AudienceSettings.Default,
                Viewers = config?.Viewers ?? prev?.Viewers ?? ViewerSettings.Default,
                // Only declarative via .podium.yml (trusted repositories): a deck cannot grant itself scripts from the UI.
                NpmScripts = source.Trusted && (config?.NpmScripts ?? prev?.NpmScripts ?? false),
                LastCommitSha = last?.Sha ?? prev?.LastCommitSha ?? sha,
                LastCommitAt = last?.CommittedAt ?? prev?.LastCommitAt ?? committedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            if (config?.Alias is { } alias && alias != deck.Alias)
            {
                // A clash with any slug or another deck's alias is logged and skipped rather than hijacking a deck.
                if (takenSlugs.Contains(alias)) log.LogWarning("Alias '{Alias}' for {Deck} is already in use; ignoring", alias, deck.Slug);
                else { deck = deck with { Alias = alias }; takenSlugs.Add(alias); }
            }

            await decks.UpsertAsync(deck, ct);
            if (prev is null) result.Added.Add(deck.Slug);

            var buildable = c.Kind is DeckKind.Slidev or DeckKind.Presenterm or DeckKind.Static or DeckKind.PowerPoint or DeckKind.Pdf;
            // Rebuild when content changed, or when the deck has never been attempted. Failed builds are not
            // retried automatically (use the Rebuild action) to avoid looping on permanently broken decks.
            var needsBuild = buildable && (forceRebuild || touched || deck.LatestBuildId is null);
            if (needsBuild)
            {
                await builds.QueueAsync(deck, source, sha, triggeredBy ?? "sync", metadata?.UnsupportedFeatures ?? [], ct);
                result.Queued.Add(deck.Slug);
            }
        }

        foreach (var (key, deck) in existing)
        {
            if (!seenKeys.Contains(key) && !deck.Archived)
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

    private async Task<DeckConfig?> ReadConfigAsync(Source source, string sha, DeckCandidate c, IReadOnlySet<string> tree, CancellationToken ct)
    {
        foreach (var name in DeckConfig.FileNames)
        {
            var path = string.IsNullOrEmpty(c.Path) ? name : $"{c.Path}/{name}";
            if (!tree.Contains(path)) continue; // no API call for the common case of no config file
            try
            {
                var text = await repos.ReadTextFileAsync(source, sha, path, ct);
                if (text is null) continue;
                var cfg = DeckConfig.Parse(text);
                if (cfg is null) log.LogWarning("Ignoring invalid {File} in {Deck}", name, c.Path);
                return cfg;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Failed reading {File} for {Deck}", name, c.Path); }
        }
        return null;
    }

    private static string DeckKey(DeckKind kind, string path, string entry)
        => DeckDetector.IsFileBased(kind) ? $"file:{(string.IsNullOrEmpty(path) ? entry : $"{path}/{entry}")}" : $"dir:{path}";

    private static bool PathIsFile(string changed, string entryPath)
    {
        changed = changed.Replace('\\', '/');
        if (string.Equals(changed, entryPath, StringComparison.Ordinal)) return true;
        // A PowerPoint deck also changes when its sibling export (same name, .pdf) changes.
        var dot = entryPath.LastIndexOf('.');
        return dot > 0 && string.Equals(changed, entryPath[..dot] + ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static string HumanizeFile(string entry)
    {
        var dot = entry.LastIndexOf('.');
        var stem = dot > 0 ? entry[..dot] : entry;
        return stem.Replace('-', ' ').Replace('_', ' ');
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
