using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Podium.Core.Discovery;

public sealed record DeckMetadata(string? Title, string? Author, string? Description, IReadOnlyList<string> Tags, IReadOnlyList<string> UnsupportedFeatures);

/// <summary>
/// Extracts title/author/etc. from Slidev or presenterm markdown, and flags features Podium cannot serve remotely.
/// </summary>
public static partial class DeckMetadataReader
{
    public static DeckMetadata ReadSlidev(string markdown)
    {
        var (fm, body) = SplitFrontmatter(markdown);
        string? title = null, author = null, info = null;
        var tags = new List<string>();

        if (fm is not null)
        {
            title = fm.GetValueOrDefault("title");
            author = fm.GetValueOrDefault("author");
            info = fm.GetValueOrDefault("info");
            if (fm.TryGetValue("tags", out var t) && !string.IsNullOrWhiteSpace(t))
                tags.AddRange(t.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }

        title ??= FirstHeading(body);

        var unsupported = new List<string>();
        if (MonacoRunRegex().IsMatch(body)) unsupported.Add("monaco-run: code runs in the audience browser only; server-side runners are unavailable remotely");

        return new DeckMetadata(Clean(title), Clean(author), Clean(info), tags, unsupported);
    }

    public static DeckMetadata ReadPresenterm(string markdown)
    {
        var (fm, body) = SplitFrontmatter(markdown);
        // presenterm title slides are usually the first slide; prefer a heading or bold line there over later slides,
        // where the first heading is often the speaker's name.
        var firstSlide = body.Split("<!-- end_slide -->", 2, StringSplitOptions.None)[0];
        var title = fm?.GetValueOrDefault("title") ?? FirstHeading(firstSlide) ?? FirstBoldLine(firstSlide);
        var author = fm?.GetValueOrDefault("author");
        var sub = fm?.GetValueOrDefault("sub_title");
        var unsupported = new List<string>();
        if (ExecRegex().IsMatch(body)) unsupported.Add("+exec code blocks are not executed in the exported HTML");
        return new DeckMetadata(Clean(title), Clean(author), Clean(sub), [], unsupported);
    }

    public static (Dictionary<string, string>? Frontmatter, string Body) SplitFrontmatter(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return (null, text);
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (null, text);
        var yaml = text[4..end];
        var body = text[(end + 4)..];
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode map) return (new(), body);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in map.Children)
            {
                if (k is YamlScalarNode ks && ks.Value is not null)
                {
                    dict[ks.Value] = v switch
                    {
                        YamlScalarNode vs => vs.Value ?? "",
                        YamlSequenceNode seq => string.Join(",", seq.Children.OfType<YamlScalarNode>().Select(s => s.Value)),
                        _ => "",
                    };
                }
            }
            return (dict, body);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return (new(), body);
        }
    }

    private static string? FirstHeading(string body)
    {
        var m = HeadingRegex().Match(body);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? FirstBoldLine(string body)
    {
        var m = BoldLineRegex().Match(body);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = InlineMarkupRegex().Replace(s, "");
        s = HeadingMarkerRegex().Replace(s, "");
        s = WhitespaceRegex().Replace(s, " ").Trim();
        if (s.Length == 0) return null;
        if (s.Length > 180)
        {
            var cut = s.LastIndexOf(' ', 179);
            s = s[..(cut > 100 ? cut : 180)].TrimEnd('.', ',', ';', ':') + "…";
        }
        return s;
    }

    [GeneratedRegex(@"^#{1,2}\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*\*\*(.+?)\*\*\s*$", RegexOptions.Multiline)]
    private static partial Regex BoldLineRegex();

    [GeneratedRegex(@"```[a-zA-Z]+\s*\{[^}]*monaco-run", RegexOptions.IgnoreCase)]
    private static partial Regex MonacoRunRegex();

    [GeneratedRegex(@"```[a-zA-Z0-9_+-]*\s*\+exec", RegexOptions.IgnoreCase)]
    private static partial Regex ExecRegex();

    [GeneratedRegex(@"<[^>]+>|[*_`]")]
    private static partial Regex InlineMarkupRegex();

    [GeneratedRegex(@"^\s*#{1,6}\s*", RegexOptions.Multiline)]
    private static partial Regex HeadingMarkerRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
