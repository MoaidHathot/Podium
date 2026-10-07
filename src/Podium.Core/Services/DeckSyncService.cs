using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Podium.Core.Abstractions;
using Podium.Core.Discovery;
using Podium.Core.Models;

namespace Podium.Core.Services;

/// <summary>
/// Reconciles a source's repository tree with the deck index and queues builds for changed decks. Also indexes the
/// talks described next to the decks (abstract.md + submissions/) and the speaker file at the repository root.
/// </summary>
public sealed partial class DeckSyncService(
    ISourceStore sources,
    IDeckStore decks,
    IRepositoryClient repos,
    BuildService builds,
    ILogger<DeckSyncService> log,
    ITalkStore? talks = null)
{
    /// <summary>Scans a source. When <paramref name="changedPaths"/> is given (webhook), only decks touching those paths are rebuilt.</summary>
    public async Task<SyncResult> SyncAsync(Source source, IReadOnlyCollection<string>? changedPaths = null, bool forceRebuild = false, string? triggeredBy = null, CancellationToken ct = default)
    {
        var (sha, committedAt) = await repos.GetHeadAsync(source, ct);
        var tree = await repos.ListTreeAsync(source, sha, ct);
        var treeSet = tree.Select(p => p.Replace('\\', '/').TrimStart('/')).ToHashSet(StringComparer.Ordinal);
        var candidates = DeckDetector.Detect(tree).ToList();
        // Directory-based decks are keyed by their directory (plus variant); file-based ones (PowerPoint, PDF) by their full entry path.
        var existing = (await decks.ListBySourceAsync(source.Id, ct)).ToDictionary(d => DeckKey(d.Kind, d.Path, d.Entry, d.Variant), StringComparer.Ordinal);
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
        bool Touched(string path) => forceRebuild || effectiveChanged is null || effectiveChanged.Any(p => PathTouchesDeck(p, path));

        var result = new SyncResult();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        // Repository-managed settings (.podium.yml), one per folder, honoured for trusted sources only. Read up front for
        // every folder that changed, because the entry override decides which file is the main deck before slugs are given.
        var configs = new Dictionary<string, DeckConfig?>(StringComparer.Ordinal);
        foreach (var dir in candidates.Select(c => c.Path).Distinct(StringComparer.Ordinal))
        {
            var anyNew = candidates.Where(c => c.Path == dir).Any(c => !existing.ContainsKey(DeckKey(c.Kind, c.Path, c.Entry, c.Variant)));
            if (source.Trusted && (anyNew || Touched(dir))) configs[dir] = await ReadConfigAsync(source, sha, dir, treeSet, ct);
        }
        candidates = ApplyEntryOverrides(candidates, configs);

        // Talks, phase one: know every talk's id before decks are written, so members can point at it.
        var talkDrafts = await LoadTalkDraftsAsync(source, sha, tree, Touched, forceRebuild, ct);
        var talkIdByLocal = talkDrafts.Values.GroupBy(t => t.LocalId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        // Aliases live in the slug namespace. Reserve every existing alias and assign all slugs up front, so an alias
        // declared by one deck can never collide with a slug minted for another deck later in the same pass.
        foreach (var a in allDecks.Select(d => d.Alias).Where(a => a is not null)) takenSlugs.Add(a!);
        var slugs = new Dictionary<DeckCandidate, string>();
        foreach (var c in candidates.Where(c => c.Variant is null))
        {
            existing.TryGetValue(DeckKey(c.Kind, c.Path, c.Entry, null), out var prevForSlug);
            var s = prevForSlug?.Slug ?? UniqueSlug(DeckDetector.IsFileBased(c.Kind) ? Slug.ForFile(source.Repo, c.Entry) : Slug.ForDeck(source.Repo, c.Path), takenSlugs);
            takenSlugs.Add(s);
            slugs[c] = s;
        }
        foreach (var c in candidates.Where(c => c.Variant is not null))
        {
            // Variants hang off their folder's main slug: "<folder-slug>-<variant>".
            existing.TryGetValue(DeckKey(c.Kind, c.Path, c.Entry, c.Variant), out var prevForSlug);
            var main = slugs.FirstOrDefault(kv => kv.Key.Path == c.Path && kv.Key.Variant is null && kv.Key.Kind == c.Kind).Value ?? Slug.ForDeck(source.Repo, c.Path);
            var s = prevForSlug?.Slug ?? UniqueSlug(Slug.ForVariant(main, c.Variant!), takenSlugs);
            takenSlugs.Add(s);
            slugs[c] = s;
        }

        var written = new List<(DeckCandidate Candidate, Deck Deck, DeckConfig? Config)>();
        var mainTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            var key = DeckKey(c.Kind, c.Path, c.Entry, c.Variant);
            seenKeys.Add(key);
            existing.TryGetValue(key, out var prev);
            var fileBased = DeckDetector.IsFileBased(c.Kind);

            var touched = prev is null || forceRebuild || effectiveChanged is null
                || effectiveChanged.Any(p => fileBased ? PathIsFile(p, c.EntryPath) : PathTouchesDeckContent(p, c.Path));
            var metadata = touched ? await ReadMetadataAsync(source, sha, c, ct) : null;

            var slug = slugs[c];

            var last = touched ? await repos.LastCommitForPathAsync(source, sha, fileBased ? c.EntryPath : c.Path, ct) : null;

            configs.TryGetValue(c.Path, out var config);
            // Folder-level settings apply to every deck in the folder, PowerPoint and PDF files included; the title and
            // alias name the folder's main source deck only (a folder of files has no single main deck).
            var folderConfig = config;
            var titleConfig = c.Variant is null && !fileBased ? folderConfig?.Title : null;
            var talkId = talkDrafts.TryGetValue(c.Path, out var homeTalk) ? homeTalk.Id
                : config?.Talk is { } joined && talkIdByLocal.TryGetValue(joined, out var joinedId) ? joinedId
                : config?.Talk is { } unknownTalk ? LogUnknownTalk(unknownTalk, c.Path) : (touched ? null : prev?.TalkId);
            // A member deck without tags of its own carries its talk's tags, so the library filters find every variant.
            var talkTags = homeTalk is { Tags.Count: > 0 } ? homeTalk.Tags : null;
            var variantTitle = c.Variant is not null && mainTitles.TryGetValue(c.Path, out var mt) ? $"{mt} ({c.Variant})" : null;
            // A file deck whose title is the one an earlier Podium generated from its file name (every hyphen a space)
            // gets the current, readable rendering; titles chosen in the UI or in .podium.yml are never touched.
            var prevTitle = fileBased && prev?.Title is { } pt && pt == LegacyHumanizeFile(c.Entry) ? null : prev?.Title;

            var deck = (prev ?? new Deck
            {
                Slug = slug,
                SourceId = source.Id,
                Path = c.Path,
                Entry = c.Entry,
                Kind = c.Kind,
                Visibility = folderConfig?.Visibility ?? Visibility.Private, // seeds new decks only; the UI wins afterwards
                ExportPdf = c.Kind is DeckKind.Slidev or DeckKind.Presenterm or DeckKind.PowerPoint or DeckKind.Pdf,
            }) with
            {
                Entry = c.Entry,
                Kind = c.Kind,
                Variant = c.Variant,
                TalkId = talkId,
                Archived = false,
                Title = titleConfig ?? metadata?.Title ?? prevTitle ?? variantTitle ?? (fileBased ? HumanizeFile(c.Entry) : Humanize(c.Path, source.Repo)),
                Author = metadata?.Author ?? prev?.Author,
                Description = metadata?.Description ?? prev?.Description,
                Tags = folderConfig?.Tags ?? metadata?.Tags ?? (prev?.Tags is { Count: > 0 } ownTags ? ownTags : null) ?? talkTags ?? prev?.Tags ?? [],
                ExportPdf = folderConfig?.ExportPdf ?? prev?.ExportPdf ?? (c.Kind is DeckKind.Slidev or DeckKind.Presenterm or DeckKind.PowerPoint or DeckKind.Pdf),
                ExportPptx = folderConfig?.ExportPptx ?? prev?.ExportPptx ?? false,
                StripNotesForViewers = folderConfig?.StripNotes ?? prev?.StripNotesForViewers ?? true,
                Audience = folderConfig?.Audience ?? prev?.Audience ?? AudienceSettings.Default,
                Viewers = folderConfig?.Viewers ?? prev?.Viewers ?? ViewerSettings.Default,
                // Only declarative via .podium.yml (trusted repositories): a deck cannot grant itself scripts from the UI.
                NpmScripts = source.Trusted && (folderConfig?.NpmScripts ?? prev?.NpmScripts ?? false),
                LastCommitSha = last?.Sha ?? prev?.LastCommitSha ?? sha,
                LastCommitAt = last?.CommittedAt ?? prev?.LastCommitAt ?? committedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            if (c.Variant is null && !fileBased) mainTitles[c.Path] = deck.Title;
            if (c.Variant is null && !fileBased && folderConfig?.Alias is { } alias && alias != deck.Alias)
            {
                // A clash with any slug or another deck's alias is logged and skipped rather than hijacking a deck.
                if (takenSlugs.Contains(alias)) log.LogWarning("Alias '{Alias}' for {Deck} is already in use; ignoring", alias, deck.Slug);
                else { deck = deck with { Alias = alias }; takenSlugs.Add(alias); }
            }

            await decks.UpsertAsync(deck, ct);
            written.Add((c, deck, config));
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

        // Talks, phase two: members (folder decks first, then joined decks), submission decks, Podium-side overlays.
        if (talks is not null)
        {
            var existingTalks = (await talks.ListBySourceAsync(source.Id, ct)).ToDictionary(t => t.Path, StringComparer.Ordinal);
            foreach (var (path, draft) in talkDrafts)
            {
                existingTalks.TryGetValue(path, out var prevTalk);
                var members = written.Where(w => w.Deck.TalkId == draft.Id).OrderBy(w => w.Deck.Path == path ? 0 : 1).ThenBy(w => w.Deck.Variant is null ? 0 : 1).ThenBy(w => w.Deck.Slug, StringComparer.Ordinal).Select(w => w.Deck).ToList();
                var mainMember = members.FirstOrDefault(m => m.Path == path && m.Variant is null) ?? members.FirstOrDefault();
                var submissions = draft.Submissions.Select(s =>
                {
                    var prevSub = prevTalk?.Submissions.FirstOrDefault(p => p.Key == s.Key);
                    return s with
                    {
                        DeckSlug = ResolveDeck(s.Deck, path, members, allDecks),
                        SessionId = prevSub?.SessionId,
                        SessionDeckSlug = prevSub?.SessionDeckSlug,
                    };
                }).ToList();
                var talk = draft with
                {
                    Title = string.IsNullOrWhiteSpace(draft.Title) ? (mainMember?.Title is { Length: > 0 } mt2 ? mt2 : Humanize(path, source.Repo)) : draft.Title,
                    Tags = draft.Tags.Count > 0 ? draft.Tags : (mainMember?.Tags ?? []),
                    Submissions = submissions,
                    DeckSlugs = members.Select(m => m.Slug).ToList(),
                    LastCommitAt = draft.LastCommitAt ?? prevTalk?.LastCommitAt ?? committedAt,
                    Archived = false,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                if (prevTalk is not null && prevTalk.Id != talk.Id) await talks.DeleteAsync(prevTalk.Id, ct); // id: changed in abstract.md
                await talks.UpsertAsync(talk, ct);
                if (prevTalk is null) result.TalksAdded.Add(talk.Id);
            }
            foreach (var (path, prevTalk) in existingTalks)
            {
                if (!talkDrafts.ContainsKey(path) && !prevTalk.Archived)
                    await talks.UpsertAsync(prevTalk with { Archived = true, DeckSlugs = [], UpdatedAt = DateTimeOffset.UtcNow }, ct);
            }

            // Speaker file at the repository root.
            var speakerPath = DeckDetector.DetectSpeaker(tree);
            if (speakerPath is null) { if (await talks.GetSpeakerAsync(source.Id, ct) is not null) await talks.DeleteSpeakerAsync(source.Id, ct); }
            else if (source.Trusted && (forceRebuild || effectiveChanged is null || effectiveChanged.Any(p => p.Replace('\\', '/').Equals(speakerPath, StringComparison.Ordinal)) || await talks.GetSpeakerAsync(source.Id, ct) is null))
            {
                try
                {
                    var text = await repos.ReadTextFileAsync(source, sha, speakerPath, ct);
                    var speaker = text is null ? null : TalkFiles.ReadSpeaker(text, source.Id);
                    if (speaker is null) log.LogWarning("Ignoring invalid speaker.md in {Source}", source.Id);
                    else await talks.UpsertSpeakerAsync(speaker, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Failed reading speaker.md for {Source}", source.Id); }
            }
        }

        await sources.UpsertAsync(source with { LastSeenSha = sha, LastScannedAt = DateTimeOffset.UtcNow }, ct);
        log.LogInformation("Synced {Source}@{Sha}: +{Added} ~{Queued} -{Archived}, {Talks} talk(s)", source.Id, sha[..7], result.Added.Count, result.Queued.Count, result.Archived.Count, talkDrafts.Count);
        return result;
    }

    private string? LogUnknownTalk(string localId, string path)
    {
        log.LogWarning("{Path}/.podium.yml refers to talk '{Talk}', but no abstract.md declares it; the deck stays on its own", path, localId);
        return null;
    }

    /// <summary>Reads abstract.md and submissions for every talk folder that is new or changed; keeps the stored talk otherwise.</summary>
    private async Task<Dictionary<string, Talk>> LoadTalkDraftsAsync(Source source, string sha, IReadOnlyList<string> tree, Func<string, bool> touched, bool force, CancellationToken ct)
    {
        var drafts = new Dictionary<string, Talk>(StringComparer.Ordinal);
        if (talks is null || !source.Trusted) return drafts;
        var existing = (await talks.ListBySourceAsync(source.Id, ct)).ToDictionary(t => t.Path, StringComparer.Ordinal);
        foreach (var tc in DeckDetector.DetectTalks(tree))
        {
            existing.TryGetValue(tc.Path, out var prev);
            if (prev is not null && !force && !touched(tc.Path)) { drafts[tc.Path] = prev; continue; }
            try
            {
                var text = await repos.ReadTextFileAsync(source, sha, tc.AbstractPath, ct);
                var talk = text is null ? null : TalkFiles.ReadTalk(text, source.Repo, tc.Path, source.Id);
                if (talk is null) { log.LogWarning("Ignoring invalid {File}", tc.AbstractPath); if (prev is not null) drafts[tc.Path] = prev; continue; }
                var subs = new List<Submission>();
                foreach (var file in tc.SubmissionFiles.Take(200))
                {
                    var st = await repos.ReadTextFileAsync(source, sha, tc.SubmissionPath(file), ct);
                    var sub = st is null ? null : TalkFiles.ReadSubmission(st, file);
                    if (sub is null) { log.LogWarning("Ignoring invalid submission {File}", tc.SubmissionPath(file)); continue; }
                    subs.Add(sub);
                }
                var last = await repos.LastCommitForPathAsync(source, sha, tc.Path, ct);
                drafts[tc.Path] = talk with { Submissions = subs.OrderByDescending(s => s.Date ?? DateOnly.MinValue).ThenBy(s => s.Key, StringComparer.Ordinal).ToList(), LastCommitAt = last?.CommittedAt };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Failed reading talk files in {Path}", tc.Path);
                if (prev is not null) drafts[tc.Path] = prev;
            }
        }
        return drafts;
    }

    /// <summary>A submission's deck reference: an entry file name in the talk folder, a repository path, a slug or an alias.</summary>
    private static string? ResolveDeck(string? reference, string talkPath, IReadOnlyList<Deck> members, IReadOnlyList<Deck> allDecks)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var r = reference.Trim().Replace('\\', '/').TrimStart('/').TrimEnd('/');
        foreach (var m in members)
        {
            if (m.Entry.Equals(r, StringComparison.OrdinalIgnoreCase) || m.EntryPath.Equals(r, StringComparison.OrdinalIgnoreCase) || m.Path.Equals(r, StringComparison.OrdinalIgnoreCase)) return m.Slug;
            if (m.Slug.Equals(r, StringComparison.OrdinalIgnoreCase) || (m.Alias is not null && m.Alias.Equals(r, StringComparison.OrdinalIgnoreCase))) return m.Slug;
            if (m.Variant is not null && m.Variant.Equals(Slug.Normalize(r), StringComparison.Ordinal)) return m.Slug;
        }
        var byPath = allDecks.FirstOrDefault(d => !d.Archived && (d.EntryPath.Equals(r, StringComparison.OrdinalIgnoreCase) || (d.Path.Equals(r, StringComparison.OrdinalIgnoreCase) && d.Variant is null) || d.Slug.Equals(r, StringComparison.OrdinalIgnoreCase) || (d.Alias is not null && d.Alias.Equals(r, StringComparison.OrdinalIgnoreCase))));
        return byPath?.Slug;
    }

    /// <summary>presenterm: <c>entry:</c> in .podium.yml names the file the plain deck URL serves; the detector's pick becomes a variant.</summary>
    private static List<DeckCandidate> ApplyEntryOverrides(List<DeckCandidate> candidates, Dictionary<string, DeckConfig?> configs)
    {
        foreach (var (dir, cfg) in configs)
        {
            if (cfg?.Entry is not { } entry) continue;
            var group = candidates.Where(c => c.Path == dir && c.Kind == DeckKind.Presenterm).ToList();
            var wanted = group.FirstOrDefault(c => c.Entry.Equals(entry, StringComparison.OrdinalIgnoreCase));
            var main = group.FirstOrDefault(c => c.Variant is null);
            if (wanted is null || main is null || wanted == main) continue;
            var stem = main.Entry[..^3];
            candidates[candidates.IndexOf(main)] = main with { Variant = Slug.Normalize(stem) };
            candidates[candidates.IndexOf(wanted)] = wanted with { Variant = null };
        }
        return candidates.OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Variant is null ? 0 : 1).ThenBy(c => c.Entry, StringComparer.Ordinal).ToList();
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

    private async Task<DeckConfig?> ReadConfigAsync(Source source, string sha, string dir, IReadOnlySet<string> tree, CancellationToken ct)
    {
        foreach (var name in DeckConfig.FileNames)
        {
            var path = string.IsNullOrEmpty(dir) ? name : $"{dir}/{name}";
            if (!tree.Contains(path)) continue; // no API call for the common case of no config file
            try
            {
                var text = await repos.ReadTextFileAsync(source, sha, path, ct);
                if (text is null) continue;
                var cfg = DeckConfig.Parse(text);
                if (cfg is null) log.LogWarning("Ignoring invalid {File} in {Deck}", name, dir);
                return cfg;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Failed reading {File} for {Deck}", name, dir); }
        }
        return null;
    }

    private static string DeckKey(DeckKind kind, string path, string entry, string? variant)
        => DeckDetector.IsFileBased(kind) ? $"file:{(string.IsNullOrEmpty(path) ? entry : $"{path}/{entry}")}" : variant is null ? $"dir:{path}" : $"dir:{path}#{variant}";

    private static bool PathIsFile(string changed, string entryPath)
    {
        changed = changed.Replace('\\', '/');
        if (string.Equals(changed, entryPath, StringComparison.Ordinal)) return true;
        // A PowerPoint deck also changes when its sibling export (same name, .pdf) changes.
        var dot = entryPath.LastIndexOf('.');
        return dot > 0 && string.Equals(changed, entryPath[..dot] + ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A readable title from a file name: underscores and word-joining hyphens become spaces, spaced separators
    /// (" - ") and hyphens between digits (2022-02) stay. "ADP Crash Course 2022-12 - Module 01 - Intro to C#.pptx"
    /// reads as written; "sample-talk.pptx" becomes "sample talk".
    /// </summary>
    public static string HumanizeFile(string entry)
    {
        var dot = entry.LastIndexOf('.');
        var stem = dot > 0 ? entry[..dot] : entry;
        var text = WordHyphen().Replace(stem.Replace('_', ' '), " ");
        return Spaces().Replace(text, " ").Trim();
    }

    /// <summary>The rule before the one above (every hyphen a space); titles equal to it were generated, not chosen.</summary>
    private static string LegacyHumanizeFile(string entry)
    {
        var dot = entry.LastIndexOf('.');
        var stem = dot > 0 ? entry[..dot] : entry;
        return stem.Replace('-', ' ').Replace('_', ' ');
    }

    /// <summary>Any file under the folder (configuration, talk files, decks alike).</summary>
    private static bool PathTouchesDeck(string changed, string deckPath)
    {
        changed = changed.Replace('\\', '/');
        if (string.IsNullOrEmpty(deckPath)) return !changed.Contains('/');
        return changed.StartsWith(deckPath + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// A change that affects what the deck builds: anything under the folder except the talk's own files (abstract.md,
    /// submissions/, speaker/notes/script and README markdown), which describe the deck without being part of it.
    /// </summary>
    private static bool PathTouchesDeckContent(string changed, string deckPath)
    {
        changed = changed.Replace('\\', '/');
        if (!PathTouchesDeck(changed, deckPath)) return false;
        var rel = string.IsNullOrEmpty(deckPath) ? changed : changed[(deckPath.Length + 1)..];
        if (rel.StartsWith("submissions/", StringComparison.OrdinalIgnoreCase)) return false;
        return rel.Contains('/') || !DeckDetector.IsReservedMarkdown(rel);
    }

    [GeneratedRegex(@"(?<![\s\d])-(?=\S)|(?<=\S)-(?![\s\d])")] private static partial Regex WordHyphen();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex Spaces();

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
    public List<string> TalksAdded { get; } = [];
}
