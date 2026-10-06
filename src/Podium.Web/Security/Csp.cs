using System.Security.Cryptography;

namespace Podium.Web.Security;

/// <summary>
/// Content-Security-Policy for Podium's own UI (library, deck management, sources, login). Scripts are allowed from
/// the origin and from inline blocks carrying the per-request nonce only; nothing is fetched from third parties.
/// Deck pages under /d/ are the deck author's HTML and are deliberately left alone (the external origin isolates
/// untrusted ones); the sync socket and static assets are not documents.
/// </summary>
public static class Csp
{
    private const string ItemKey = "podium.cspNonce";

    public static bool AppliesTo(PathString path)
        => !path.StartsWithSegments("/d") && !path.StartsWithSegments("/_podium") && !path.StartsWithSegments("/ws") && !path.StartsWithSegments("/api");

    /// <summary>The nonce for the current response (created on first use); pages put it on their inline script tags.</summary>
    public static string Nonce(HttpContext http)
    {
        if (http.Items.TryGetValue(ItemKey, out var existing) && existing is string s) return s;
        // base64url: CSP accepts it and Razor does not entity-encode it inside attributes (plain base64's "+" becomes &#x2B;).
        var nonce = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18));
        http.Items[ItemKey] = nonce;
        return nonce;
    }

    /// <summary>Images: Podium's own, inline data URIs, GitHub avatars and raw repository files (speaker photo in speaker.md).</summary>
    public static string HeaderValue(string nonce) =>
        $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https://avatars.githubusercontent.com https://raw.githubusercontent.com; " +
        "font-src 'self'; connect-src 'self'; frame-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
}
