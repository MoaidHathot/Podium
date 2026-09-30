using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Podium.Core;

public static partial class Slug
{
    /// <summary>Produces a URL-safe slug for a deck: "{repo}-{last-path-segment}" or just "{repo}" for root decks.</summary>
    public static string ForDeck(string repo, string path)
    {
        var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        var raw = string.IsNullOrEmpty(last) ? repo : $"{repo}-{last}";
        return Normalize(raw);
    }

    public static string Normalize(string input)
    {
        var s = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }
        var slug = Dashes().Replace(sb.ToString(), "-").Trim('-');
        if (slug.Length > 80) slug = slug[..80].TrimEnd('-');
        return slug.Length == 0 ? "deck" : slug;
    }

    public static bool IsValid(string slug) => slug.Length is > 0 and <= 96 && ValidSlug().IsMatch(slug);

    [GeneratedRegex("-{2,}")]
    private static partial Regex Dashes();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex ValidSlug();
}
