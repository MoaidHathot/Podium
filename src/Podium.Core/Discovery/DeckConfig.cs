using Podium.Core.Models;
using YamlDotNet.RepresentationModel;

namespace Podium.Core.Discovery;

/// <summary>
/// Optional, repository-managed deck settings read from <c>.podium.yml</c> next to the deck entry (trusted sources only).
/// Declarative fields (title, alias, tags, exports, notes policy) are re-applied on every sync; <c>visibility</c> only
/// seeds a newly discovered deck, because it is security-sensitive and the owner's choice in the UI must win.
/// </summary>
public sealed record DeckConfig(string? Title, string? Alias, IReadOnlyList<string>? Tags, bool? ExportPdf, bool? ExportPptx, bool? StripNotes, Visibility? Visibility, bool? NpmScripts)
{
    public static readonly string[] FileNames = [".podium.yml", ".podium.yaml", "podium.yml"];

    public static DeckConfig? Parse(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode map) return null;
            string? Str(string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var n) && n is YamlScalarNode s ? s.Value : null;
            bool? Bool(string key) => Str(key) is { } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase) : null;
            IReadOnlyList<string>? tags = null;
            if (map.Children.TryGetValue(new YamlScalarNode("tags"), out var tn))
            {
                tags = tn switch
                {
                    YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Select(x => x.Value ?? "").Where(x => x.Length > 0).Select(x => x.Trim().ToLowerInvariant()).Distinct().Take(20).ToList(),
                    YamlScalarNode sc => (sc.Value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).Distinct().Take(20).ToList(),
                    _ => null,
                };
            }
            Visibility? vis = Str("visibility") is { } vs && Enum.TryParse<Visibility>(vs, true, out var parsed) ? parsed : null;
            var alias = Str("alias") is { } a ? Core.Slug.Normalize(a) : null;
            var title = Str("title")?.Trim();
            return new DeckConfig(string.IsNullOrEmpty(title) ? null : title.Length > 200 ? title[..200] : title, alias, tags, Bool("exportPdf") ?? Bool("export_pdf"), Bool("exportPptx") ?? Bool("export_pptx"), Bool("stripNotes") ?? Bool("strip_notes"), vis, Bool("npmScripts") ?? Bool("npm_scripts"));
        }
        catch (YamlDotNet.Core.YamlException) { return null; }
    }
}
