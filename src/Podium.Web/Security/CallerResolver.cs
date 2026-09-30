using System.Security.Claims;
using Microsoft.Extensions.Options;
using Podium.Core.Security;
using Podium.Web.Configuration;

namespace Podium.Web.Security;

public static class PodiumClaims
{
    public const string GitHubId = "urn:podium:github_id";
    public const string Login = "urn:podium:login";
    public const string Avatar = "urn:podium:avatar";
    public const string OwnerPolicy = "Owner";
}

/// <summary>Turns the ASP.NET principal into the domain <see cref="Caller"/>. The owner is pinned by numeric GitHub id.</summary>
public sealed class CallerResolver(IOptions<PodiumOptions> options)
{
    public Caller Resolve(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return Caller.Anonymous;
        var idStr = user.FindFirstValue(PodiumClaims.GitHubId) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(idStr, out var id)) return Caller.Anonymous;
        var login = user.FindFirstValue(PodiumClaims.Login) ?? user.Identity.Name;
        return new Caller($"github:{id}", id == options.Value.OwnerGitHubId, login);
    }
}
