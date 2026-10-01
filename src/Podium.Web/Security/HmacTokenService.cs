using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Podium.Core.Services;
using Podium.Web.Configuration;

namespace Podium.Web.Security;

/// <summary>
/// HMAC-SHA256 tokens of the form "{expiresUnix}.{base64url(sig)}" bound to (purpose, subject).
/// Constant-time comparison; no state.
/// </summary>
public sealed class HmacTokenService(IOptions<PodiumOptions> options) : IBuildTokenService
{
    private readonly byte[] _key = DecodeKey(options.Value.SigningKey);

    string IBuildTokenService.Issue(string deckSlug, string buildId, TimeSpan lifetime) => IssueFor("build", $"{deckSlug}\n{buildId}", lifetime);

    bool IBuildTokenService.Validate(string token, string deckSlug, string buildId) => ValidateFor("build", $"{deckSlug}\n{buildId}", token);

    public string IssueFor(string purpose, string subject, TimeSpan lifetime)
    {
        var exp = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        var sig = Sign(purpose, subject, exp);
        return $"{exp}.{Base64Url(sig)}";
    }

    public bool ValidateFor(string purpose, string subject, string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return false;
        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0) return false;
        if (!long.TryParse(token.AsSpan(0, dot), out var exp)) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow) return false;
        byte[] provided;
        try { provided = FromBase64Url(token[(dot + 1)..]); }
        catch (FormatException) { return false; }
        var expected = Sign(purpose, subject, exp);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private byte[] Sign(string purpose, string subject, long exp)
        => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{purpose}\n{subject}\n{exp}"));

    /// <summary>Signature over an arbitrary body (expiry is the caller's responsibility, e.g. inside the body).</summary>
    public string SignFor(string purpose, string body)
        => Base64Url(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{purpose}\n{body}")));

    public bool VerifyFor(string purpose, string body, string signature)
    {
        byte[] provided;
        try { provided = FromBase64Url(signature); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{purpose}\n{body}"));
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private static byte[] DecodeKey(string key)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(key); }
        catch (FormatException) { bytes = Encoding.UTF8.GetBytes(key); }
        if (bytes.Length < 32) throw new InvalidOperationException("Podium:SigningKey must be at least 32 bytes (base64 of 32 random bytes recommended).");
        return bytes;
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}
