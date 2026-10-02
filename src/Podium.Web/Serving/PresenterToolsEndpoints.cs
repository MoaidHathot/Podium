using Microsoft.Net.Http.Headers;
using Podium.Core.Models;
using Podium.Core.Security;
using Podium.Web.Security;
using QRCoder;

namespace Podium.Web.Serving;

/// <summary>
/// /d/{slug}/qr.svg  : QR code of the deck URL (same access rules as the deck itself).
/// /d/{slug}/remote  : phone remote for presenters (owner / Present grantees): prev/next, slide counter, timer.
/// Both are Podium-generated pages, never author code, so they are served from the main origin.
/// </summary>
public static class PresenterToolsEndpoints
{
    public static IEndpointRouteBuilder MapPresenterTools(this IEndpointRouteBuilder app)
    {
        app.MapGet("/d/{slug}/qr.svg", async (string slug, HttpContext http, DeckAccessService access, CallerResolver callers, Microsoft.Extensions.Options.IOptions<Configuration.PodiumOptions> options, CancellationToken ct) =>
        {
            var caller = callers.Resolve(http.User);
            var result = await access.EvaluateAsync(http, slug, ArtifactKind.Site, caller, ct);
            if (result.Deck is null || result.Decision != AccessDecision.Allow) return Results.NotFound();
            // Always the public hostname: audience phones must not be sent to the platform FQDN.
            var url = $"{options.Value.PublicBaseUrl.ToString().TrimEnd('/')}/d/{slug}/";
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            var svg = new SvgQRCode(data).GetGraphic(8, "#0b0d12", "#ffffff", drawQuietZones: true);
            http.Response.Headers[HeaderNames.CacheControl] = "private, max-age=3600";
            return Results.Content(svg, "image/svg+xml; charset=utf-8");
        }).RequireRateLimiting("probe");

        app.MapGet("/d/{slug}/remote", async (string slug, HttpContext http, DeckAccessService access, CallerResolver callers, CancellationToken ct) =>
        {
            var caller = callers.Resolve(http.User);
            var result = await access.EvaluateAsync(http, slug, ArtifactKind.Site, caller, ct);
            if (result.Deck is null) return Results.NotFound();
            if (result.Decision == AccessDecision.RequireLogin) return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(http.Request.Path));
            if (result.Decision != AccessDecision.Allow || !result.CanPresent) return Results.NotFound();
            if (result.Deck.Kind != DeckKind.Slidev) return Results.NotFound();
            http.Response.Headers[HeaderNames.CacheControl] = "no-store";
            return Results.Content(RemotePage(result.Deck), "text/html; charset=utf-8");
        });
        return app;
    }

    private static string RemotePage(Deck deck)
    {
        var title = System.Net.WebUtility.HtmlEncode(deck.Title);
        var slug = deck.Slug;
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover,user-scalable=no">
            <meta name="theme-color" content="#0b0d12"><title>Remote · {{title}}</title>
            <link rel="stylesheet" href="/css/remote.css">
            </head><body data-slug="{{slug}}">
            <header><div class="title">{{title}}</div><div class="conn" id="conn" aria-live="polite">connecting…</div></header>
            <main>
              <div class="counter"><span id="page">–</span><span class="of">/ <span id="total">–</span></span><span class="clicks" id="clicks"></span></div>
              <div class="pad">
                <button class="nav prev" id="prev" aria-label="Previous"><svg viewBox="0 0 24 24" width="40" height="40" fill="none" stroke="currentColor" stroke-width="2.5"><path d="m15 5-7 7 7 7"/></svg></button>
                <button class="nav next" id="next" aria-label="Next"><svg viewBox="0 0 24 24" width="40" height="40" fill="none" stroke="currentColor" stroke-width="2.5"><path d="m9 5 7 7-7 7"/></svg></button>
              </div>
              <div class="row">
                <button class="small" id="first">⇤ First</button>
                <button class="small" id="timer-toggle">▶ Timer</button>
                <button class="small" id="timer-reset" title="Reset timer">↺</button>
                <span class="timer" id="timer">00:00</span>
              </div>
              <div class="hint">Drives every open instance of this deck (audience view, projector). Keyboard: ← → Space.</div>
            </main>
            <script src="/js/remote.js"></script>
            </body></html>
            """;
    }
}
