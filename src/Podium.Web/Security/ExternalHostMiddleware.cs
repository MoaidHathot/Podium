using Microsoft.Extensions.Options;
using Podium.Web.Configuration;

namespace Podium.Web.Security;

/// <summary>
/// Runs on the external deck origin only. Exchanges a ?podium_vt= token for a host-only cookie, turns the cookie into
/// the request identity, and confines the origin to deck serving (no owner UI/API). The main session cookie never
/// exists on this host, so deck code running here cannot act as the owner.
/// </summary>
public sealed class ExternalHostMiddleware(RequestDelegate next, ViewTokenService tokens, IOptions<PodiumOptions> options)
{
    public const string CookieName = "podium_view";
    public const string QueryParam = "podium_vt";
    // /api/builds: builder callbacks are bearer-token authenticated and session-free, and the callback base URL is
    // typically this very host (platform FQDN).
    private static readonly PathString[] AllowedPrefixes = ["/d", "/_podium", "/ws/sync", "/api/builds", "/healthz", "/css", "/js", "/favicon.svg"];
    // Podium-generated presenter tools are never served from the external origin (they are for the owner, who has no session there).
    private static readonly System.Text.RegularExpressions.Regex ToolPaths = new("^/d/[a-z0-9][a-z0-9-]*/(remote|qr\\.svg|notes\\.json|slides\\.jpg|slides\\.json|session\\.json)$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex VersionApi = new("^/api/decks/[a-z0-9][a-z0-9-]*/version$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task InvokeAsync(HttpContext http)
    {
        if (!tokens.IsExternalHost(http.Request))
        {
            await next(http);
            return;
        }

        // 1. Token handshake: set the cookie and drop the parameter from the URL.
        if (http.Request.Query.TryGetValue(QueryParam, out var raw))
        {
            var payload = tokens.Validate(raw.ToString());
            if (payload is null)
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("Invalid or expired view token. Open the deck from the library again.");
                return;
            }
            http.Response.Cookies.Append(CookieName, raw.ToString(), new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = DateTimeOffset.FromUnixTimeSeconds(payload.Exp),
            });
            var query = System.Web.HttpUtility.ParseQueryString(http.Request.QueryString.Value ?? "");
            query.Remove(QueryParam);
            var clean = http.Request.Path + (query.Count > 0 ? "?" + query : "");
            http.Response.Redirect(clean);
            return;
        }

        // 2. Only deck-related paths exist on this origin.
        // The only API reachable here is the live-reload version probe; everything else (owner API, UI, login) stays on the main origin.
        if ((!AllowedPrefixes.Any(p => http.Request.Path.StartsWithSegments(p)) && !VersionApi.IsMatch(http.Request.Path.Value ?? "")) || ToolPaths.IsMatch(http.Request.Path.Value ?? ""))
        {
            // Send people who land here by accident back to the real app.
            http.Response.Redirect(options.Value.PublicBaseUrl.ToString().TrimEnd('/') + http.Request.Path + http.Request.QueryString);
            return;
        }

        // 3. Identity comes from the view cookie, never from a session.
        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        if (http.Request.Cookies.TryGetValue(CookieName, out var cookie) && tokens.Validate(cookie) is { } p)
        {
            http.User = tokens.ToPrincipal(p);
            http.Items["podium.viewLinkSlug"] = p.LinkSlug;
        }
        http.Items["podium.externalHost"] = true;

        await next(http);
    }
}
