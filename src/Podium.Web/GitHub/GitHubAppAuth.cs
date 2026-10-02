using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Octokit;
using Podium.Web.Configuration;

namespace Podium.Web.GitHub;

/// <summary>
/// Produces GitHub clients: App-level (JWT), per-installation (cached installation tokens), or anonymous/PAT for
/// public repositories. Tokens live in memory only.
/// </summary>
public sealed class GitHubAppAuth(IOptions<GitHubOptions> options, ILogger<GitHubAppAuth> log)
{
    private static readonly ProductHeaderValue Product = new("Podium", "1.0");
    private readonly ConcurrentDictionary<long, (string Token, DateTimeOffset ExpiresAt)> _installationTokens = new();
    private RSA? _rsa;

    public GitHubOptions Options => options.Value;

    public bool IsConfigured => options.Value.AppConfigured || !string.IsNullOrWhiteSpace(options.Value.Token);

    /// <summary>Client authenticated as the App itself (list installations, etc.).</summary>
    public GitHubClient CreateAppClient()
    {
        if (!options.Value.AppConfigured) throw new InvalidOperationException("GitHub App is not configured (GitHub:AppId / GitHub:PrivateKeyPem).");
        return new GitHubClient(Product) { Credentials = new Credentials(CreateAppJwt(), AuthenticationType.Bearer) };
    }

    public async Task<string> GetInstallationTokenAsync(long installationId, CancellationToken ct)
    {
        if (_installationTokens.TryGetValue(installationId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
            return cached.Token;

        var app = CreateAppClient();
        var token = await app.GitHubApps.CreateInstallationToken(installationId).WaitAsync(ct);
        _installationTokens[installationId] = (token.Token, token.ExpiresAt);
        log.LogDebug("Minted installation token for {Installation}, expires {Exp}", installationId, token.ExpiresAt);
        return token.Token;
    }

    public async Task<GitHubClient> CreateInstallationClientAsync(long installationId, CancellationToken ct)
        => new GitHubClient(Product) { Credentials = new Credentials(await GetInstallationTokenAsync(installationId, ct)) };

    /// <summary>
    /// Mints a token that can only read the contents of one repository. Handed to the builder for cloning, so even if
    /// deck code captured it, it could not reach any other repository the App is installed on.
    /// </summary>
    public async Task<string> CreateRepositoryScopedTokenAsync(long installationId, string repositoryName, CancellationToken ct)
    {
        var app = CreateAppClient();
        var body = new { repositories = new[] { repositoryName }, permissions = new { contents = "read" } };
        var token = await app.Connection.Post<AccessToken>(new Uri($"app/installations/{installationId}/access_tokens", UriKind.Relative), body, "application/vnd.github+json", "application/json", TimeSpan.FromSeconds(30), ct);
        return token.Body.Token;
    }

    /// <summary>
    /// Client for repositories without an installation (public repos). Prefers a dev PAT, then any installation token
    /// (valid for public content), then anonymous.
    /// </summary>
    public async Task<GitHubClient> CreatePublicClientAsync(long? anyInstallationId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.Value.Token))
            return new GitHubClient(Product) { Credentials = new Credentials(options.Value.Token) };
        if (anyInstallationId is { } id && options.Value.AppConfigured)
        {
            try { return await CreateInstallationClientAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Falling back to anonymous GitHub access"); }
        }
        return new GitHubClient(Product);
    }

    public string? GetDevToken() => string.IsNullOrWhiteSpace(options.Value.Token) ? null : options.Value.Token;

    private string CreateAppJwt()
    {
        _rsa ??= LoadKey(options.Value.PrivateKeyPem!);
        var now = DateTimeOffset.UtcNow;
        var creds = new SigningCredentials(new RsaSecurityKey(_rsa), SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: options.Value.AppId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            claims: [new Claim("iat", now.AddSeconds(-60).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)],
            expires: now.AddMinutes(9).UtcDateTime,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static RSA LoadKey(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem.Replace("\\n", "\n", StringComparison.Ordinal));
        return rsa;
    }
}
