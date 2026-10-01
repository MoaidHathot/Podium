using Podium.Core.Models;

namespace Podium.Core.Discovery;

/// <summary>A deck candidate found by scanning a repository tree.</summary>
public sealed record DeckCandidate(string Path, string Entry, DeckKind Kind)
{
    public string EntryPath => string.IsNullOrEmpty(Path) ? Entry : $"{Path}/{Entry}";
}

/// <summary>
/// Pure function over a list of repository file paths: finds decks.
/// Slidev: directory containing slides.md.
/// presenterm: directory with config.yaml and a markdown file (prefers main.md).
/// PowerPoint: every .pptx file in a directory that is not a Slidev/presenterm deck.
/// Pdf: every standalone .pdf (no .pptx or .html with the same base name) in such a directory.
/// Static: a committed .html export in a directory with no source deck (one per directory).
/// GitPitch (PITCHME.md) directories are skipped.
/// </summary>
public static class DeckDetector
{
    private static readonly string[] IgnoredSegments = ["node_modules", ".git", "dist", ".slidev", "bin", "obj", ".vscode", ".idea", ".github", ".podium-addon"];

    public static IReadOnlyList<DeckCandidate> Detect(IEnumerable<string> paths)
    {
        var files = paths
            .Select(p => p.Replace('\\', '/').TrimStart('/'))
            .Where(p => !p.Split('/').Any(seg => IgnoredSegments.Contains(seg, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var byDir = files
            .GroupBy(Dir, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(FileName).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);

        var result = new List<DeckCandidate>();

        foreach (var (dir, names) in byDir)
        {
            if (names.Contains("slides.md"))
            {
                result.Add(new DeckCandidate(dir, "slides.md", DeckKind.Slidev));
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
                var md = PickPresentermEntry(names);
                if (md is not null)
                {
                    result.Add(new DeckCandidate(dir, md, DeckKind.Presenterm));
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

        return result.OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Entry, StringComparer.Ordinal).ToList();
    }

    /// <summary>True for kinds whose identity is a single file, so several can live in one directory.</summary>
    public static bool IsFileBased(DeckKind kind) => kind is DeckKind.PowerPoint or DeckKind.Pdf;

    private static string? PickPresentermEntry(HashSet<string> names)
    {
        if (names.Contains("main.md")) return "main.md";
        if (names.Contains("presentation.md")) return "presentation.md";
        var mds = names.Where(n => n.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !n.Equals("README.md", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        return mds.Count == 1 ? mds[0] : mds.LastOrDefault();
    }

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
}
