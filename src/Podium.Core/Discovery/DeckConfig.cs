using Podium.Core.Models;
using YamlDotNet.RepresentationModel;

namespace Podium.Core.Discovery;

/// <summary>
/// Optional, repository-managed deck settings read from <c>.podium.yml</c> next to the deck entry (trusted sources only).
/// Declarative fields (title, alias, tags, exports, notes policy, audience features) are re-applied on every sync;
/// <c>visibility</c> only seeds a newly discovered deck, because it is security-sensitive and the owner's choice in
/// the UI must win.
/// <code>
/// audience:
///   reactions: true
///   questions: true
///   polls: true
///   floatReactions: true   # emoji float across the projector
///   nicknames: true        # questions may carry a name
/// viewers:
///   presenterView: false   # non-presenters may open the presenter view / notes viewer
///   browseAhead: false     # while live, non-presenters may move past your slide
/// </code>
/// </summary>
public sealed record DeckConfig(string? Title, string? Alias, IReadOnlyList<string>? Tags, bool? ExportPdf, bool? ExportPptx, bool? StripNotes, Visibility? Visibility, bool? NpmScripts, AudienceSettings? Audience = null, ViewerSettings? Viewers = null)
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
            bool? Bool(string key) => Str(key) is { } v ? ParseBool(v) : null;
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
            AudienceSettings? audience = null;
            if (map.Children.TryGetValue(new YamlScalarNode("audience"), out var an))
            {
                if (an is YamlMappingNode am)
                {
                    string? AStr(params string[] keys) { foreach (var k in keys) if (am.Children.TryGetValue(new YamlScalarNode(k), out var v) && v is YamlScalarNode s) return s.Value; return null; }
                    bool ABool(bool fallback, params string[] keys) => AStr(keys) is { } v ? ParseBool(v) : fallback;
                    var d = AudienceSettings.Default;
                    audience = new AudienceSettings(ABool(d.Reactions, "reactions"), ABool(d.Questions, "questions"), ABool(d.Polls, "polls"), ABool(d.FloatReactions, "floatReactions", "float_reactions"), ABool(d.Nicknames, "nicknames"));
                }
                else if (an is YamlScalarNode asc && !ParseBool(asc.Value ?? ""))
                {
                    // `audience: false` switches everything off at once.
                    audience = new AudienceSettings(false, false, false, false, false);
                }
            }
            ViewerSettings? viewers = null;
            if (map.Children.TryGetValue(new YamlScalarNode("viewers"), out var vn) && vn is YamlMappingNode vm)
            {
                string? VStr(params string[] keys) { foreach (var k in keys) if (vm.Children.TryGetValue(new YamlScalarNode(k), out var v) && v is YamlScalarNode s) return s.Value; return null; }
                bool VBool(bool fallback, params string[] keys) => VStr(keys) is { } v ? ParseBool(v) : fallback;
                var dv = ViewerSettings.Default;
                viewers = new ViewerSettings(VBool(dv.PresenterView, "presenterView", "presenter_view"), VBool(dv.BrowseAhead, "browseAhead", "browse_ahead"));
            }
            return new DeckConfig(string.IsNullOrEmpty(title) ? null : title.Length > 200 ? title[..200] : title, alias, tags, Bool("exportPdf") ?? Bool("export_pdf"), Bool("exportPptx") ?? Bool("export_pptx"), Bool("stripNotes") ?? Bool("strip_notes"), vis, Bool("npmScripts") ?? Bool("npm_scripts"), audience, viewers);
        }
        catch (YamlDotNet.Core.YamlException) { return null; }
    }

    private static bool ParseBool(string v) => v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase);
}
