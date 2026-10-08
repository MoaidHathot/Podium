using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Podium.Core.Security;
using Podium.Web.Configuration;
using Podium.Web.Security;

namespace Podium.Web.Pages;

/// <summary>
/// Sign-in. A browser that signed in before (see <see cref="KnownDevice"/>) is sent straight to GitHub, which either
/// recognises it and comes right back or asks for credentials itself; the page is only shown to browsers Podium has
/// never seen, after a failed or declined round-trip, or when asked for explicitly (sign-out, ?prompt=1).
/// </summary>
public sealed class LoginModel(IOptions<GitHubOptions> gitHub, IWebHostEnvironment env, IConfiguration config, CallerResolver callers) : PageModel
{
    public Caller Caller { get; private set; } = Caller.Anonymous;
    public string ReturnUrl { get; private set; } = "/";
    public bool OAuthConfigured => gitHub.Value.OAuthConfigured;
    public bool DevLogin => env.IsDevelopment() && config.GetValue<bool>("Auth:AllowDevLogin");
    /// <summary>What went wrong on the previous attempt, if anything: "failed" or "denied".</summary>
    public string? Error { get; private set; }
    /// <summary>True when this browser signed in before and the page is shown anyway (error or prompt).</summary>
    public bool Known { get; private set; }

    public IActionResult OnGet(string? returnUrl, string? error, string? prompt)
    {
        Caller = callers.Resolve(User);
        ReturnUrl = !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.StartsWith("/\\", StringComparison.Ordinal) ? returnUrl : "/";
        Error = error is "failed" or "denied" ? error : null;
        Known = KnownDevice.IsKnown(HttpContext);
        // Already signed in as the owner: there is nothing to do here.
        if (Caller.IsOwner) return LocalRedirect(ReturnUrl);
        // Known browser, nothing went wrong, not asked to show the page: continue to GitHub without a detour.
        if (Known && Error is null && prompt is null && OAuthConfigured && !Caller.IsAuthenticated)
            return Redirect($"/login/github?returnUrl={Uri.EscapeDataString(ReturnUrl)}");
        return Page();
    }
}
