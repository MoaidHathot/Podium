namespace Podium.Web.Security;

/// <summary>
/// A hint that this browser has signed in to Podium before. It carries no identity (its value is a constant) and
/// grants nothing; it only lets the login page skip itself and go straight to GitHub, which still authenticates.
/// Set at sign-in, cleared by an explicit sign-out. Lives a year so a phone that is used once a month still skips
/// the page.
/// </summary>
public static class KnownDevice
{
    public const string CookieName = "podium_known";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    public static bool IsKnown(HttpContext http) => http.Request.Cookies.TryGetValue(CookieName, out var v) && v == "1";

    public static void Remember(HttpContext http)
        => http.Response.Cookies.Append(CookieName, "1", Options(http, DateTimeOffset.UtcNow + Lifetime));

    public static void Forget(HttpContext http)
        => http.Response.Cookies.Append(CookieName, "", Options(http, DateTimeOffset.UnixEpoch));

    private static CookieOptions Options(HttpContext http, DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = http.Request.IsHttps,
        Path = "/",
        Expires = expires,
        IsEssential = true,
    };
}
