using Podium.Core.Models;

namespace Podium.Core.Discovery;

/// <summary>A deck candidate found by scanning a repository tree.</summary>
public sealed record DeckCandidate(string Path, string Entry, DeckKind Kind);

/// <summary>
/// Pure function over a list of repository file paths: finds directories that look like decks.
/// Slidev: directory containing slides.md (and usually package.json).
/// presenterm: directory with config.yaml referencing the presenterm schema, or a *.md next to config.yaml.
/// GitPitch: directory with PITCHME.md.
/// Static: a committed .html export when no source deck exists in the same directory.
/// </summary>
public static class DeckDetector
{
    private static readonly string[] IgnoredSegments = ["node_modules", ".git", "dist", ".slidev", "bin", "obj", ".vscode", ".idea"];

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

            // A committed HTML export with no source deck and no Node project in the same directory is served as-is.
            var html = names
                .Where(n => n.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !n.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (html is not null && !names.Contains("package.json"))
            {
                result.Add(new DeckCandidate(dir, html, DeckKind.Static));
            }
        }

        return result.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
    }

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
}
