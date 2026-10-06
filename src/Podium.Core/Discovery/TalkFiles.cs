using System.Globalization;
using System.Text.RegularExpressions;
using Podium.Core.Models;
using YamlDotNet.RepresentationModel;

namespace Podium.Core.Discovery;

/// <summary>
/// Readers for the talk files kept next to a deck (trusted sources only):
/// <list type="bullet">
///   <item><c>abstract.md</c>: front matter (id, title, level, duration, status, public, tags) + the canonical abstract,
///     followed by <c>## Named</c> parts (Short abstract, Outline, Takeaways, Bio, ...).</item>
///   <item><c>submissions/*.md</c>: front matter (event, date, status, format, duration, title, deck, location, url,
///     recording, notes) + the abstract as submitted (empty = canonical) and optional parts (## Bio as submitted).</item>
///   <item><c>speaker.md</c> at the repository root: name, tagline, photo, links + the bio, with ## Short bio etc.</item>
/// </list>
/// Everything is plain data; rendering escapes it. Unknown keys are ignored, malformed YAML yields null.
/// </summary>
public static partial class TalkFiles
{
    public static readonly string[] Statuses = ["available", "draft", "retired"];
    public static readonly string[] SubmissionStatuses = ["submitted", "accepted", "declined", "delivered", "cancelled"];
    public static readonly string[] Formats = ["talk", "workshop", "lightning", "keynote", "panel", "course", "webinar"];
    private const int MaxBody = 60_000;

    /// <summary>Front matter as a YAML mapping (or empty), the body before the first ## heading, and the ## parts.</summary>
    public static (YamlMappingNode Front, string Body, IReadOnlyList<TalkPart> Parts)? Split(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\uFEFF');
        if (text.Length > MaxBody * 2) text = text[..(MaxBody * 2)];
        YamlMappingNode front = new();
        var body = text;
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end < 0) return null;
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(text[4..end]));
                if (stream.Documents.Count > 0)
                {
                    if (stream.Documents[0].RootNode is YamlMappingNode map) front = map;
                    else return null;
                }
            }
            catch (YamlDotNet.Core.YamlException) { return null; }
            body = text[(end + 4)..];
            var nl = body.IndexOf('\n');
            body = nl < 0 ? "" : body[(nl + 1)..]; // drop the rest of the closing fence line
        }
        var parts = new List<TalkPart>();
        var main = new List<string>();
        string? current = null;
        var buffer = new List<string>();
        var inFence = false;
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            var h = !inFence ? PartHeading().Match(line) : Match.Empty;
            if (h.Success)
            {
                Flush();
                current = h.Groups[1].Value.Trim();
                continue;
            }
            (current is null ? main : buffer).Add(line);
        }
        Flush();
        return (front, Trim(string.Join('\n', main)), parts);

        void Flush()
        {
            if (current is null) { buffer.Clear(); return; }
            var md = Trim(string.Join('\n', buffer));
            parts.Add(new TalkPart(current, md));
            buffer.Clear();
        }
    }

    public static Talk? ReadTalk(string markdown, string repo, string path, string sourceId)
    {
        if (Split(markdown) is not { } s) return null;
        var (fm, body, parts) = s;
        var localId = Str(fm, "id") is { } rawId && Slug.Normalize(rawId) is { Length: >= 2 } nid ? nid : null;
        var status = Str(fm, "status")?.Trim().ToLowerInvariant();
        var durations = Ints(fm, "duration").Concat(Ints(fm, "durations")).Where(d => d is > 0 and <= 600).Distinct().OrderBy(d => d).ToList();
        var folder = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return new Talk
        {
            Id = Slug.ForTalk(repo, path, localId),
            LocalId = localId ?? Slug.Normalize(string.IsNullOrEmpty(folder) ? repo : folder),
            SourceId = sourceId,
            Path = path,
            Title = Truncate(Str(fm, "title")?.Trim(), 200) ?? "",
            Level = Truncate(Str(fm, "level")?.Trim().ToLowerInvariant(), 40),
            Durations = durations,
            Status = status is not null && Statuses.Contains(status) ? status : "available",
            Public = Bool(fm, "public") ?? false,
            Tags = Strings(fm, "tags").Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 40).Distinct().Take(20).ToList(),
            Abstract = body,
            Parts = parts,
        };
    }

    public static Submission? ReadSubmission(string markdown, string fileName)
    {
        if (Split(markdown) is not { } s) return null;
        var (fm, body, parts) = s;
        var key = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? fileName[..^3] : fileName;
        var status = Str(fm, "status")?.Trim().ToLowerInvariant();
        var format = Str(fm, "format")?.Trim().ToLowerInvariant();
        return new Submission
        {
            Key = key,
            Event = Truncate(Str(fm, "event")?.Trim(), 200) ?? "",
            Date = Date(fm, "date") ?? DeckDetector.DateOf(fileName),
            Status = status is not null && SubmissionStatuses.Contains(status) ? status : "submitted",
            Format = format is not null && Formats.Contains(format) ? format : "talk",
            Duration = Ints(fm, "duration").FirstOrDefault(d => d is > 0 and <= 600) is { } d and > 0 ? d : null,
            Title = Truncate(Str(fm, "title")?.Trim(), 200),
            Deck = Truncate(Str(fm, "deck")?.Trim(), 300),
            Location = Truncate(Str(fm, "location")?.Trim(), 200),
            Url = Url(Str(fm, "url")),
            Recording = Url(Str(fm, "recording")),
            Notes = Truncate(Str(fm, "notes")?.Trim(), 2000),
            Abstract = string.IsNullOrWhiteSpace(body) ? null : body,
            Parts = parts,
        };
    }

    public static Speaker? ReadSpeaker(string markdown, string sourceId)
    {
        if (Split(markdown) is not { } s) return null;
        var (fm, body, parts) = s;
        var links = new List<KeyValuePair<string, string>>();
        if (fm.Children.TryGetValue(new YamlScalarNode("links"), out var ln))
        {
            if (ln is YamlMappingNode lm)
            {
                foreach (var (k, v) in lm.Children)
                    if (k is YamlScalarNode ks && v is YamlScalarNode vs && Url(vs.Value) is { } u) links.Add(new(Truncate(ks.Value, 40) ?? "link", u));
            }
            else if (ln is YamlSequenceNode ls)
            {
                foreach (var item in ls.Children)
                    if (item is YamlScalarNode itemNode && Url(itemNode.Value) is { } itemUrl) links.Add(new(new Uri(itemUrl).Host.Replace("www.", ""), itemUrl));
            }
        }
        return new Speaker
        {
            SourceId = sourceId,
            Name = Truncate(Str(fm, "name")?.Trim(), 120) ?? "",
            Tagline = Truncate(Str(fm, "tagline")?.Trim(), 200),
            Photo = Url(Str(fm, "photo")),
            Links = links.Take(12).ToList(),
            Bio = body,
            Parts = parts,
        };
    }

    // ---- YAML helpers -------------------------------------------------------------------------------------------
    private static string? Str(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var n) && n is YamlScalarNode s ? s.Value : null;
    private static bool? Bool(YamlMappingNode map, string key) => Str(map, key) is { } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase) : null;
    private static IEnumerable<string> Strings(YamlMappingNode map, string key)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var n)) return [];
        return n switch
        {
            YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Select(x => x.Value ?? "").Where(x => x.Length > 0),
            YamlScalarNode sc => (sc.Value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            _ => [],
        };
    }
    private static IEnumerable<int> Ints(YamlMappingNode map, string key) => Strings(map, key).Select(s => int.TryParse(s.Trim().TrimEnd('m').Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1).Where(i => i > 0);
    private static DateOnly? Date(YamlMappingNode map, string key)
    {
        var v = Str(map, key)?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (DateOnly.TryParseExact(v, ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd", "yyyy_MM_dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)) return DateOnly.FromDateTime(dt.UtcDateTime);
        if (DateOnly.TryParseExact(v, ["yyyy-MM", "yyyy/MM", "MMMM yyyy", "MMM yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)) return month;
        return null;
    }
    private static string? Url(string? v)
    {
        v = v?.Trim();
        if (string.IsNullOrEmpty(v) || v.Length > 2000) return null;
        return Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.ToString() : null;
    }
    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
    private static string Trim(string s) { s = s.Trim('\n', ' ', '\t'); return s.Length <= MaxBody ? s : s[..MaxBody]; }

    [GeneratedRegex(@"^##\s+(?!#)(.+?)\s*#*\s*$")]
    private static partial Regex PartHeading();
}
