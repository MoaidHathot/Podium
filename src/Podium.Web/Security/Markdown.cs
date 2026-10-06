using System.Text;
using System.Text.RegularExpressions;

namespace Podium.Web.Security;

/// <summary>
/// Renders the Markdown subset used in talk, submission and speaker files to HTML, escaping first and only then
/// adding markup: paragraphs, headings (### and deeper; ## is consumed by the part splitter), bullet and numbered
/// lists, block quotes, fenced and inline code, **bold**, *emphasis*, http(s) links and bare URLs. Nothing the author
/// writes becomes HTML, so a repository can never inject script or styling into Podium pages.
/// </summary>
public static partial class Markdown
{
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var para = new List<string>();
        string? list = null; // "ul" | "ol"
        var inFence = false;
        var fence = new StringBuilder();
        var quote = new List<string>();

        void FlushPara()
        {
            if (para.Count == 0) return;
            sb.Append("<p>").Append(Inline(string.Join(' ', para.Select(l => l.Trim())))).Append("</p>\n");
            para.Clear();
        }
        void CloseList() { if (list is not null) { sb.Append("</").Append(list).Append(">\n"); list = null; } }
        void FlushQuote()
        {
            if (quote.Count == 0) return;
            sb.Append("<blockquote>").Append(ToHtml(string.Join('\n', quote))).Append("</blockquote>\n");
            quote.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (inFence)
            {
                if (line.StartsWith("```", StringComparison.Ordinal)) { inFence = false; sb.Append("<pre><code>").Append(Escape(fence.ToString().TrimEnd('\n'))).Append("</code></pre>\n"); fence.Clear(); }
                else fence.Append(raw).Append('\n');
                continue;
            }
            if (line.StartsWith("```", StringComparison.Ordinal)) { FlushPara(); CloseList(); FlushQuote(); inFence = true; continue; }
            if (line.StartsWith('>')) { FlushPara(); CloseList(); quote.Add(line.TrimStart('>').TrimStart()); continue; }
            FlushQuote();
            if (line.Length == 0) { FlushPara(); CloseList(); continue; }

            var h = Heading().Match(line);
            if (h.Success)
            {
                FlushPara(); CloseList();
                var level = Math.Clamp(h.Groups[1].Value.Length, 3, 6); // ## is a part boundary upstream; keep the hierarchy below the page's own headings
                sb.Append($"<h{level}>").Append(Inline(h.Groups[2].Value.Trim())).Append($"</h{level}>\n");
                continue;
            }
            var li = Bullet().Match(line);
            if (li.Success)
            {
                FlushPara();
                var kind = char.IsDigit(li.Groups[1].Value[0]) ? "ol" : "ul";
                if (list != kind) { CloseList(); list = kind; sb.Append('<').Append(kind).Append(">\n"); }
                sb.Append("<li>").Append(Inline(li.Groups[2].Value.Trim())).Append("</li>\n");
                continue;
            }
            if (Rule().IsMatch(line)) { FlushPara(); CloseList(); sb.Append("<hr>\n"); continue; }
            if (list is not null && line.StartsWith("  ", StringComparison.Ordinal))
            {
                // Continuation of the previous list item.
                var idx = sb.ToString().LastIndexOf("</li>", StringComparison.Ordinal);
                if (idx >= 0) { sb.Insert(idx, ' ' + Inline(line.Trim())); continue; }
            }
            CloseList();
            para.Add(line);
        }
        if (inFence) sb.Append("<pre><code>").Append(Escape(fence.ToString().TrimEnd('\n'))).Append("</code></pre>\n");
        FlushPara(); CloseList(); FlushQuote();
        return sb.ToString();
    }

    /// <summary>Plain text (markup stripped), for meta descriptions and copy-as-text.</summary>
    public static string ToText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var text = markdown.Replace("\r\n", "\n");
        text = FenceBlock().Replace(text, m => m.Groups[1].Value);
        text = Link().Replace(text, "$1");
        text = text.Replace("**", "").Replace("__", "");
        text = Regex.Replace(text, @"(?<![\w*])\*(?!\s)([^*\n]+?)(?<!\s)\*(?!\w)", "$1");
        text = Regex.Replace(text, @"`([^`\n]+)`", "$1");
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*[-*+]\s+", "- ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^>\s?", "", RegexOptions.Multiline);
        return text.Trim();
    }

    public static string Escape(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>
    /// Inline markup: code, links, bare URLs, bold, emphasis. Author text is escaped before any markup is added; code
    /// spans and generated anchors are parked in slots (control characters, which the input never contains) so the
    /// bold/emphasis passes cannot reach into an attribute.
    /// </summary>
    internal static string Inline(string text)
    {
        var slots = new List<string>();
        string Park(string html) { slots.Add(html); return $"\u0001{slots.Count - 1}\u0001"; }
        var protectedText = Code().Replace(Control().Replace(text, ""), m => Park($"<code>{Escape(m.Groups[1].Value)}</code>"));
        var e = Escape(protectedText);
        e = Link().Replace(e, m => SafeUrl(System.Net.WebUtility.HtmlDecode(m.Groups[2].Value)) is { } url ? Park($"<a href=\"{Escape(url)}\" rel=\"noopener noreferrer\" target=\"_blank\">{m.Groups[1].Value}</a>") : m.Groups[1].Value);
        e = BareUrl().Replace(e, m => SafeUrl(System.Net.WebUtility.HtmlDecode(m.Value)) is { } url ? Park($"<a href=\"{Escape(url)}\" rel=\"noopener noreferrer\" target=\"_blank\">{m.Value}</a>") : m.Value);
        e = Bold().Replace(e, "<strong>$1</strong>");
        e = Em().Replace(e, "$1<em>$2</em>");
        e = Slot().Replace(e, m => slots[int.Parse(m.Groups[1].Value)]);
        return e;
    }

    /// <summary>The URL when it is an absolute http(s) or mailto link, else null. Use for every author-supplied href/src.</summary>
    public static string? SafeLink(string? candidate)
        => candidate is not null && Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp || u.Scheme == "mailto") ? u.ToString() : null;

    private static string? SafeUrl(string candidate) => SafeLink(candidate);

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*$")] private static partial Regex Heading();
    [GeneratedRegex(@"^\s*([-*+]|\d{1,3}[.)])\s+(.+)$")] private static partial Regex Bullet();
    [GeneratedRegex(@"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$")] private static partial Regex Rule();
    [GeneratedRegex(@"`([^`\n]+)`")] private static partial Regex Code();
    [GeneratedRegex("\u0001(\\d+)\u0001")] private static partial Regex Slot();
    [GeneratedRegex("[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")] private static partial Regex Control();
    [GeneratedRegex(@"\[([^\]\n]+)\]\(((?:[^()\s]|\([^()\s]*\))+)\)")] private static partial Regex Link();
    [GeneratedRegex(@"(?<![""'=>\w/])https?://[^\s<>""']+[^\s<>""'.,;:!?)]")] private static partial Regex BareUrl();
    [GeneratedRegex(@"\*\*(?!\s)(.+?)(?<!\s)\*\*")] private static partial Regex Bold();
    [GeneratedRegex(@"(^|[\s(>])\*(?!\s)([^*\n]+?)(?<!\s)\*(?![\w*])")] private static partial Regex Em();
    [GeneratedRegex(@"```[^\n]*\n([\s\S]*?)```")] private static partial Regex FenceBlock();
}
