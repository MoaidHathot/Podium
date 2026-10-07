using System.Text.RegularExpressions;
using Podium.Core.Models;

namespace Podium.Core.Discovery;

/// <summary>A deck candidate found by scanning a repository tree.</summary>
/// <param name="Variant">Label when this is one of several entry files in the folder; null for the folder's main deck.</param>
public sealed record DeckCandidate(string Path, string Entry, DeckKind Kind, string? Variant = null)
{
    public string EntryPath => string.IsNullOrEmpty(Path) ? Entry : $"{Path}/{Entry}";
}

/// <summary>A folder with an abstract.md: a talk, plus the files under its submissions/ folder.</summary>
public sealed record TalkCandidate(string Path, IReadOnlyList<string> SubmissionFiles)
{
    public string AbstractPath => string.IsNullOrEmpty(Path) ? "abstract.md" : $"{Path}/abstract.md";
    public string SubmissionPath(string file) => string.IsNullOrEmpty(Path) ? $"submissions/{file}" : $"{Path}/submissions/{file}";
}

/// <summary>
/// Pure function over a list of repository file paths: finds decks.
/// Slidev: directory containing slides.md; slides.&lt;variant&gt;.md files next to it are variant decks.
/// presenterm: directory with config.yaml and markdown; main.md / presentation.md is the main deck, otherwise the
///   newest dated file (2025_11_18.md, 2025-11-18.md); every other markdown file is a variant deck named by its stem.
/// PowerPoint: every .pptx file in a directory that is not a Slidev/presenterm deck.
/// Pdf: every standalone .pdf (no .pptx or .html with the same base name) in such a directory.
/// Static: a committed .html export in a directory with no source deck (one per directory).
/// GitPitch (PITCHME.md) directories are skipped. abstract.md, speaker.md, README.md and friends are never decks.
/// </summary>
public static partial class DeckDetector
{
    private static readonly string[] IgnoredSegments = ["node_modules", ".git", "dist", ".slidev", "bin", "obj", ".vscode", ".idea", ".github", ".podium-addon"];
    /// <summary>Markdown files that describe a deck or the speaker rather than being slides.</summary>
    private static readonly HashSet<string> ReservedMarkdown = new(StringComparer.OrdinalIgnoreCase) { "README.md", "abstract.md", "speaker.md", "bio.md", "notes.md", "script.md", "CHANGELOG.md", "LICENSE.md", "CONTRIBUTING.md" };

    public static IReadOnlyList<DeckCandidate> Detect(IEnumerable<string> paths)
    {
        var files = Clean(paths);
        var byDir = files
            .GroupBy(Dir, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(FileName).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);

        var result = new List<DeckCandidate>();

        foreach (var (dir, names) in byDir)
        {
            // Files under a talk's submissions/ folder are records, never decks.
            if (FileName(dir).Equals("submissions", StringComparison.OrdinalIgnoreCase) && byDir.TryGetValue(Dir(dir), out var parent) && parent.Contains("abstract.md"))
                continue;

            if (names.Contains("slides.md"))
            {
                result.Add(new DeckCandidate(dir, "slides.md", DeckKind.Slidev));
                foreach (var n in names.Where(n => SlidevVariant().IsMatch(n)).OrderBy(n => n, StringComparer.Ordinal))
                    result.Add(new DeckCandidate(dir, n, DeckKind.Slidev, Slug.Normalize(SlidevVariant().Match(n).Groups[1].Value)));
                continue;
            }

            if (names.Contains("PITCHME.md"))
            {
                // Legacy GitPitch decks (and their shared fragments) are not renderable; skip them so they do not
                // clutter the library. DeckKind.GitPitch is kept for a possible future renderer.
                continue;
            }

            if (names.Contains("config.yaml") || names.Contains("config.yml"))
            {
                var mds = names.Where(IsSlideMarkdown).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var main = PickPresentermEntry(mds);
                if (main is not null)
                {
                    result.Add(new DeckCandidate(dir, main, DeckKind.Presenterm));
                    foreach (var other in mds.Where(m => !m.Equals(main, StringComparison.OrdinalIgnoreCase)))
                        result.Add(new DeckCandidate(dir, other, DeckKind.Presenterm, Slug.Normalize(BaseName(other))));
                    continue;
                }
            }

            var pptx = names.Where(n => n.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("~$", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var p in pptx)
                result.Add(new DeckCandidate(dir, p, DeckKind.PowerPoint));

            var taken = pptx.Select(BaseName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var htmls = names.Where(n => n.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !n.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var h in htmls) taken.Add(BaseName(h));

            foreach (var pdf in names.Where(n => n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                // A PDF next to a .pptx/.html of the same name is that deck's export, not a deck of its own.
                if (taken.Contains(BaseName(pdf))) continue;
                result.Add(new DeckCandidate(dir, pdf, DeckKind.Pdf));
            }

            // A committed HTML export with no source deck and no Node project in the same directory is served as-is.
            if (htmls.Count > 0 && !names.Contains("package.json"))
                result.Add(new DeckCandidate(dir, htmls[0], DeckKind.Static));
        }

        return result.OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Variant is null ? 0 : 1).ThenBy(c => c.Entry, StringComparer.Ordinal).ToList();
    }

    /// <summary>Folders holding an abstract.md, with the markdown files of their submissions/ folder (sorted).</summary>
    public static IReadOnlyList<TalkCandidate> DetectTalks(IEnumerable<string> paths)
    {
        var files = Clean(paths);
        var set = files.ToHashSet(StringComparer.Ordinal);
        var talks = new List<TalkCandidate>();
        foreach (var abs in files.Where(f => FileName(f).Equals("abstract.md", StringComparison.OrdinalIgnoreCase)))
        {
            var dir = Dir(abs);
            var subDir = string.IsNullOrEmpty(dir) ? "submissions" : $"{dir}/submissions";
            var subs = files.Where(f => Dir(f).Equals(subDir, StringComparison.Ordinal) && f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !FileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase))
                .Select(FileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            talks.Add(new TalkCandidate(dir, subs));
        }
        return talks.OrderBy(t => t.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>The repository-root speaker.md, if any.</summary>
    public static string? DetectSpeaker(IEnumerable<string> paths)
        => Clean(paths).FirstOrDefault(p => p.Equals("speaker.md", StringComparison.OrdinalIgnoreCase));

    /// <summary>True for kinds whose identity is a single file, so several can live in one directory.</summary>
    public static bool IsFileBased(DeckKind kind) => kind is DeckKind.PowerPoint or DeckKind.Pdf;

    /// <summary>
    /// Which markdown file is the presenterm deck's main entry: main.md, then presentation.md, then the newest
    /// dated file name (yyyy_mm_dd / yyyy-mm-dd anywhere in the stem), then the only file, then the last by name.
    /// </summary>
    public static string? PickPresentermEntry(IReadOnlyList<string> mds)
    {
        if (mds.Count == 0) return null;
        var main = mds.FirstOrDefault(m => m.Equals("main.md", StringComparison.OrdinalIgnoreCase)) ?? mds.FirstOrDefault(m => m.Equals("presentation.md", StringComparison.OrdinalIgnoreCase));
        if (main is not null) return main;
        var dated = mds.Select(m => (Name: m, Date: DateOf(m))).Where(x => x.Date is not null).OrderByDescending(x => x.Date).ThenByDescending(x => x.Name, StringComparer.Ordinal).ToList();
        if (dated.Count > 0) return dated[0].Name;
        return mds.Count == 1 ? mds[0] : mds[^1];
    }

    /// <summary>A date written in a file name (2025_11_18, 2025-11-18, 20251118), or null.</summary>
    public static DateOnly? DateOf(string fileName)
    {
        var m = DatedName().Match(BaseName(fileName));
        if (!m.Success) return null;
        return int.TryParse(m.Groups[1].Value, out var y) && int.TryParse(m.Groups[2].Value, out var mo) && int.TryParse(m.Groups[3].Value, out var d)
            && mo is >= 1 and <= 12 && d is >= 1 and <= 31 && y is >= 1990 and <= 2100 ? new DateOnly(y, mo, Math.Min(d, DateTime.DaysInMonth(y, mo))) : null;
    }

    private static bool IsSlideMarkdown(string name) => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !ReservedMarkdown.Contains(name) && !name.StartsWith('.');
    /// <summary>Markdown files that describe a deck or the speaker (README, abstract, speaker, bio, notes, script...) rather than being slides.</summary>
    public static bool IsReservedMarkdown(string fileName) => ReservedMarkdown.Contains(fileName);

    private static List<string> Clean(IEnumerable<string> paths) => paths
        .Select(p => p.Replace('\\', '/').TrimStart('/'))
        .Where(p => !p.Split('/').Any(seg => IgnoredSegments.Contains(seg, StringComparer.OrdinalIgnoreCase)))
        .ToList();

    private static string Dir(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    private static string FileName(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    private static string BaseName(string fileName)
    {
        var i = fileName.LastIndexOf('.');
        return i <= 0 ? fileName : fileName[..i];
    }

    [GeneratedRegex(@"^slides\.([A-Za-z0-9][A-Za-z0-9._-]*)\.md$", RegexOptions.IgnoreCase)]
    private static partial Regex SlidevVariant();

    [GeneratedRegex(@"(?<![0-9])(\d{4})[-_.]?(\d{2})[-_.]?(\d{2})(?![0-9])")]
    private static partial Regex DatedName();
}
