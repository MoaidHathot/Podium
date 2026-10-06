using System.Text.Json;

namespace Podium.Core.Services;

/// <summary>One slide's extracted text (text.json entry).</summary>
public sealed record SlidePage(int Index, string? Title, string Text);

public enum CompareKind { Same, Changed, OnlyLeft, OnlyRight }

/// <summary>A row of the side-by-side view: an aligned pair, or a slide present on one side only.</summary>
public sealed record CompareRow(CompareKind Kind, SlidePage? Left, SlidePage? Right, double Similarity);

public sealed record CompareResult(IReadOnlyList<CompareRow> Rows, int Same, int Changed, int OnlyLeft, int OnlyRight, double Similarity);

/// <summary>A word-level diff token: ' ' unchanged, '-' only in the left text, '+' only in the right text.</summary>
public readonly record struct DiffToken(char Op, string Text);

/// <summary>
/// Compares two decks by their slide text: aligns slides with a weighted longest-common-subsequence (slides stay in
/// order; a pair may align when its word sets overlap enough) and reports which slides are identical, reworded, or
/// present on one side only. Pure functions over text.json; no model calls, no images.
/// </summary>
public static class DeckCompare
{
    public const int MaxPages = 400;
    /// <summary>Word overlap (Jaccard) below which two slides are not the same slide at all.</summary>
    public const double MatchThreshold = 0.45;
    /// <summary>Word overlap above which two aligned slides count as identical.</summary>
    public const double SameThreshold = 0.97;

    public static IReadOnlyList<SlidePage> ParseText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    public static async Task<IReadOnlyList<SlidePage>> ParseTextAsync(Stream json, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(json, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    private static IReadOnlyList<SlidePage> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return [];
        var pages = new List<SlidePage>();
        foreach (var p in root.EnumerateArray())
        {
            if (pages.Count >= MaxPages) break;
            var index = p.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : pages.Count + 1;
            var title = p.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var text = p.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
            if (text.Length > 20000) text = text[..20000];
            pages.Add(new SlidePage(index, string.IsNullOrWhiteSpace(title) ? null : title, text));
        }
        return pages;
    }

    public static CompareResult Compare(IReadOnlyList<SlidePage> left, IReadOnlyList<SlidePage> right)
    {
        var n = left.Count;
        var m = right.Count;
        var lw = left.Select(Words).ToArray();
        var rw = right.Select(Words).ToArray();
        var sim = new double[n, m];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < m; j++)
                sim[i, j] = Jaccard(lw[i], rw[j]);

        // Weighted LCS: maximise the summed similarity of aligned pairs, keeping slide order on both sides.
        var dp = new double[n + 1, m + 1];
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
            {
                var best = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                var s = sim[i - 1, j - 1];
                if (s >= MatchThreshold) best = Math.Max(best, dp[i - 1, j - 1] + s);
                dp[i, j] = best;
            }

        var rows = new List<CompareRow>();
        int a = n, b = m;
        while (a > 0 || b > 0)
        {
            if (a > 0 && b > 0 && sim[a - 1, b - 1] >= MatchThreshold && Math.Abs(dp[a, b] - (dp[a - 1, b - 1] + sim[a - 1, b - 1])) < 1e-9)
            {
                var s = sim[a - 1, b - 1];
                rows.Add(new CompareRow(s >= SameThreshold ? CompareKind.Same : CompareKind.Changed, left[a - 1], right[b - 1], s));
                a--; b--;
            }
            else if (b > 0 && (a == 0 || dp[a, b - 1] >= dp[a - 1, b]))
            {
                rows.Add(new CompareRow(CompareKind.OnlyRight, null, right[b - 1], 0));
                b--;
            }
            else
            {
                rows.Add(new CompareRow(CompareKind.OnlyLeft, left[a - 1], null, 0));
                a--;
            }
        }
        rows.Reverse();
        var same = rows.Count(r => r.Kind == CompareKind.Same);
        var changed = rows.Count(r => r.Kind == CompareKind.Changed);
        var onlyLeft = rows.Count(r => r.Kind == CompareKind.OnlyLeft);
        var onlyRight = rows.Count(r => r.Kind == CompareKind.OnlyRight);
        var overall = n + m == 0 ? 1 : rows.Where(r => r.Left is not null && r.Right is not null).Sum(r => r.Similarity) * 2 / (n + m);
        return new CompareResult(rows, same, changed, onlyLeft, onlyRight, Math.Round(overall, 3));
    }

    /// <summary>Word-level diff of two texts (LCS over words, capped for very long slides).</summary>
    public static IReadOnlyList<DiffToken> WordDiff(string left, string right, int maxWords = 600)
    {
        var a = Tokens(left).Take(maxWords).ToArray();
        var b = Tokens(right).Take(maxWords).ToArray();
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                dp[i, j] = string.Equals(Fold(a[i]), Fold(b[j]), StringComparison.Ordinal) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var result = new List<DiffToken>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (string.Equals(Fold(a[x]), Fold(b[y]), StringComparison.Ordinal)) { result.Add(new(' ', b[y])); x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) { result.Add(new('-', a[x])); x++; }
            else { result.Add(new('+', b[y])); y++; }
        }
        while (x < a.Length) result.Add(new('-', a[x++]));
        while (y < b.Length) result.Add(new('+', b[y++]));
        return result;
    }

    private static IEnumerable<string> Tokens(string text) => text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
    private static string Fold(string word) => new(word.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static HashSet<string> Words(SlidePage page)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in Tokens((page.Title ?? "") + " " + page.Text))
        {
            var f = Fold(w);
            if (f.Length > 0) set.Add(f);
        }
        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1; // two empty (image-only) slides look the same
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }
}
