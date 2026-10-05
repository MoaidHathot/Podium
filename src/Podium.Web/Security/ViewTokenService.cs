using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Podium.Web.Configuration;

namespace Podium.Web.Security;

/// <summary>Identity and grants carried by a view token on the external deck origin.</summary>
public sealed record ViewTokenPayload(string? Principal, bool IsOwner, string? DisplayName, string? LinkSlug, long Exp, string? LinkId = null);

/// <summary>
/// Self-contained, HMAC-signed tokens that let the external deck origin know who the viewer is without sharing the
/// main session cookie. Format: base64url(json) + "." + base64url(hmac).
/// </summary>
public sealed class ViewTokenService(HmacTokenService hmac, IOptions<PodiumOptions> options)
{
    private const string Purpose = "view";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public string Issue(string? principal, bool isOwner, string? displayName, string? linkSlug, string? linkId = null)
    {
        var payload = new ViewTokenPayload(principal, isOwner, displayName, linkSlug, DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds(), linkSlug is null ? null : linkId);
        var json = JsonSerializer.Serialize(payload);
        var body = Base64Url(Encoding.UTF8.GetBytes(json));
        return body + "." + hmac.SignFor(Purpose, body);
    }

    public ViewTokenPayload? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2048) return null;
        var dot = token.LastIndexOf('.');
        if (dot <= 0) return null;
        var body = token[..dot];
        if (!hmac.VerifyFor(Purpose, body, token[(dot + 1)..])) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<ViewTokenPayload>(FromBase64Url(body));
            if (payload is null || DateTimeOffset.FromUnixTimeSeconds(payload.Exp) < DateTimeOffset.UtcNow) return null;
            return payload;
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
    }

    /// <summary>Builds the ClaimsPrincipal the rest of the app understands from a validated token.</summary>
    public ClaimsPrincipal ToPrincipal(ViewTokenPayload payload)
    {
        var claims = new List<Claim> { new(PodiumClaims.ViewToken, "1") };
        if (payload.Principal is { } p && p.StartsWith("github:", StringComparison.Ordinal))
        {
            var id = p["github:".Length..];
            claims.Add(new Claim(PodiumClaims.GitHubId, id));
            claims.Add(new Claim(ClaimTypes.NameIdentifier, id));
            claims.Add(new Claim(ClaimTypes.Name, payload.DisplayName ?? id));
            claims.Add(new Claim(PodiumClaims.Login, payload.DisplayName ?? id));
        }
        if (payload.LinkSlug is { } s) claims.Add(new Claim(PodiumClaims.ViewLinkSlug, s));
        // An anonymous link viewer still gets an (unauthenticated) identity object carrying the link grant.
        var identity = payload.Principal is null ? new ClaimsIdentity(claims) : new ClaimsIdentity(claims, "podium-view");
        return new ClaimsPrincipal(identity);
    }

    public bool IsExternalHost(HttpRequest request)
    {
        var ext = options.Value.ExternalBaseUrl;
        return ext is not null && string.Equals(request.Host.Value, ext.IsDefaultPort ? ext.Host : $"{ext.Host}:{ext.Port}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Short-lived bearer for exactly one artifact of one deck (used for third-party viewers that fetch files themselves).</summary>
    public string IssueFileToken(string slug, Podium.Core.Models.ArtifactKind kind, TimeSpan lifetime)
        => hmac.IssueFor("file", $"{slug}\n{kind}", lifetime);

    public bool ValidateFileToken(string? token, string slug, Podium.Core.Models.ArtifactKind kind)
        => !string.IsNullOrEmpty(token) && hmac.ValidateFor("file", $"{slug}\n{kind}", token);

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}
