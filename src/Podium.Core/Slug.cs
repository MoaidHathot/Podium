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

    /// <summary>Slug for a file-based deck (PowerPoint, PDF): "{repo}-{file-name-without-extension}".</summary>
    public static string ForFile(string repo, string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var stem = dot > 0 ? fileName[..dot] : fileName;
        return Normalize($"{repo}-{stem}");
    }

    /// <summary>Slug for a variant deck: the folder's slug plus the variant label.</summary>
    public static string ForVariant(string baseSlug, string variant) => Normalize($"{baseSlug}-{variant}");

    /// <summary>Talk id: "{repo}-{folder}" by default, or "{repo}-{id}" when the talk declares its own id.</summary>
    public static string ForTalk(string repo, string path, string? localId)
        => string.IsNullOrWhiteSpace(localId) ? ForDeck(repo, path) : Normalize($"{repo}-{localId}");

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
