using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Core.Security;
using Podium.Web.Security;
using QRCoder;

namespace Podium.Web.Serving;

/// <summary>
/// Podium-generated presenter tooling (never author code, so always served from the main origin):
///   /d/{slug}/qr.svg      QR code of the deck URL (same access rules as the deck itself)
///   /d/{slug}/remote      phone remote for presenters (owner / Present grantees)
///   /d/{slug}/notes.json  speaker notes per slide, presenters only; the audience's site variant has them stripped
///   /d/{slug}/slides.jpg  all slides tiled into one image (+ /slides.json geometry); follows the site's visibility
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

        // Speaker notes: the one artifact that must never reach a viewer. Owner / Present grantees only, main origin
        // only (ExternalHostMiddleware bounces the path), never cached by shared caches.
        app.MapGet("/d/{slug}/notes.json", async (string slug, HttpContext http, DeckAccessService access, IArtifactStore artifacts, CallerResolver callers, CancellationToken ct) =>
        {
            var caller = callers.Resolve(http.User);
            var result = await access.EvaluateAsync(http, slug, ArtifactKind.Site, caller, ct);
            if (result.Deck is null || result.Decision != AccessDecision.Allow || !result.CanPresent) return Results.NotFound();
            if (result.Deck.CurrentBuildId is null || !result.Deck.CurrentHasNotes) return Results.NotFound();
            var file = await artifacts.OpenArtifactAsync(slug, result.Deck.CurrentBuildId, ArtifactKind.Notes, ct);
            if (file is null) return Results.NotFound();
            http.Response.Headers[HeaderNames.CacheControl] = "private, no-store";
            http.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            return Results.Stream(file.Content, "application/json; charset=utf-8");
        }).RequireRateLimiting("probe");

        // Passcode-protected share links: a tiny interstitial. GET renders the form; POST verifies, admits (signed
        // cookie) and continues to the deck. Rate limited per IP like login, and the passcode never appears in a URL.
        app.MapGet("/d/{slug}/unlock", async (string slug, [FromQuery] string? share, [FromQuery] string? next, HttpContext http, DeckAccessService access, IShareLinkStore links, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!Podium.Core.Slug.IsValid(slug) || string.IsNullOrEmpty(share) || share.Length > 64) return Results.NotFound();
            var deck = await access.GetDeckAsync(slug, ct);
            var link = await links.GetAsync(share, ct);
            if (deck is null || link is null || link.DeckSlug != slug || link.Revoked || link.PasscodeHash is null) return Results.NotFound();
            http.Response.Headers[HeaderNames.CacheControl] = "no-store";
            return Results.Content(UnlockPage(deck, share, SafeNext(next, slug), wrong: false, antiforgery.GetAndStoreTokens(http)), "text/html; charset=utf-8");
        }).RequireRateLimiting("auth");

        app.MapPost("/d/{slug}/unlock", async (string slug, HttpContext http, DeckAccessService access, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!Podium.Core.Slug.IsValid(slug)) return Results.NotFound();
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
            var form = await http.Request.ReadFormAsync(ct);
            var share = form["share"].ToString();
            var passcode = form["passcode"].ToString();
            var next = SafeNext(form["next"].ToString(), slug);
            if (string.IsNullOrEmpty(share) || share.Length > 64) return Results.NotFound();
            if (await access.AdmitWithPasscodeAsync(http, slug, share, passcode, ct)) return Results.Redirect(next);
            var deck = await access.GetDeckAsync(slug, ct);
            if (deck is null) return Results.NotFound();
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            http.Response.Headers[HeaderNames.CacheControl] = "no-store";
            return Results.Content(UnlockPage(deck, share, next, wrong: true, antiforgery.GetAndStoreTokens(http)), "text/html; charset=utf-8");
        }).RequireRateLimiting("auth");

        // Access requests. A signed-in visitor who cannot open a deck lands here. The page looks exactly the same
        // whether the deck exists or not, so it never reveals which slugs are real; a request is only stored for a
        // real Shared/Private deck the caller lacks access to.
        app.MapGet("/d/{slug}/request-access", async (string slug, HttpContext http, DeckAccessService access, CallerResolver callers, IAccessRequestStore requests, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!Podium.Core.Slug.IsValid(slug)) return Results.NotFound();
            var caller = callers.Resolve(http.User);
            if (!caller.IsAuthenticated) return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(http.Request.Path));
            if (caller.IsOwner) return Results.Redirect($"/decks/{slug}");
            var result = await access.EvaluateAsync(http, slug, ArtifactKind.Site, caller, ct);
            if (result.Decision == AccessDecision.Allow) return Results.Redirect($"/d/{slug}/");
            var existing = await requests.GetAsync(slug, caller.Principal!, ct);
            http.Response.Headers[HeaderNames.CacheControl] = "no-store";
            return Results.Content(RequestAccessPage(slug, caller, existing?.Status, antiforgery.GetAndStoreTokens(http)), "text/html; charset=utf-8");
        }).RequireRateLimiting("auth");

        app.MapPost("/d/{slug}/request-access", async (string slug, HttpContext http, DeckAccessService access, CallerResolver callers, IAccessRequestStore requests, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!Podium.Core.Slug.IsValid(slug)) return Results.NotFound();
            var caller = callers.Resolve(http.User);
            if (!caller.IsAuthenticated || caller.IsOwner) return Results.Forbid();
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
            var form = await http.Request.ReadFormAsync(ct);
            var message = form["message"].ToString().Trim();
            if (message.Length > 500) message = message[..500];
            var result = await access.EvaluateAsync(http, slug, ArtifactKind.Site, caller, ct);
            // Store only when there is something to grant; otherwise behave identically (constant response).
            if (result.Deck is { Archived: false } deck && result.Decision != AccessDecision.Allow && deck.Visibility is Visibility.Shared or Visibility.Private)
            {
                var existing = await requests.GetAsync(slug, caller.Principal!, ct);
                if (existing is null || existing.Status != AccessRequestStatus.Pending)
                    await requests.UpsertAsync(new AccessRequest { DeckSlug = slug, Principal = caller.Principal!, DisplayName = caller.DisplayName, Message = string.IsNullOrEmpty(message) ? null : message }, ct);
            }
            return Results.Redirect($"/d/{slug}/request-access?sent=1");
        }).RequireRateLimiting("auth");

        app.MapGet("/d/{slug}/slides.jpg", (string slug, HttpContext http, DeckAccessService access, IArtifactStore artifacts, CallerResolver callers, CancellationToken ct)
            => ServeSheet(slug, ArtifactKind.SlideSheet, "image/jpeg", http, access, artifacts, callers, ct)).RequireRateLimiting("probe");
        app.MapGet("/d/{slug}/slides.json", (string slug, HttpContext http, DeckAccessService access, IArtifactStore artifacts, CallerResolver callers, CancellationToken ct)
            => ServeSheet(slug, ArtifactKind.SlideSheetMeta, "application/json; charset=utf-8", http, access, artifacts, callers, ct)).RequireRateLimiting("probe");
        return app;
    }

    /// <summary>The slide sheet shows slide content, so it follows the site's visibility (like the thumbnail).</summary>
    private static async Task<IResult> ServeSheet(string slug, ArtifactKind kind, string contentType, HttpContext http, DeckAccessService access, IArtifactStore artifacts, CallerResolver callers, CancellationToken ct)
    {
        var caller = callers.Resolve(http.User);
        var result = await access.EvaluateAsync(http, slug, ArtifactKind.Thumbnail, caller, ct);
        if (result.Deck is null || result.Decision != AccessDecision.Allow || result.Deck.CurrentBuildId is null || !result.Deck.CurrentHasSlideSheet) return Results.NotFound();
        var file = await artifacts.OpenArtifactAsync(slug, result.Deck.CurrentBuildId, kind, ct);
        if (file is null) return Results.NotFound();
        http.Response.Headers[HeaderNames.CacheControl] = "private, max-age=86400";
        http.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        http.Response.Headers["X-Podium-Build"] = result.Deck.CurrentBuildId;
        return Results.Stream(file.Content, contentType, lastModified: file.LastModified,
            entityTag: file.ETag is null ? null : new EntityTagHeaderValue(DeckServingEndpoints.QuoteEtag(file.ETag)));
    }

    /// <summary>Only same-deck paths may be the continuation target (no open redirects).</summary>
    private static string SafeNext(string? next, string slug)
        => !string.IsNullOrEmpty(next) && next.StartsWith($"/d/{slug}", StringComparison.Ordinal) && !next.StartsWith("//", StringComparison.Ordinal) && !next.Contains('\\') && next.Length < 2000 ? next : $"/d/{slug}/";

    private static string UnlockPage(Deck deck, string share, string next, bool wrong, AntiforgeryTokenSet tokens)
    {
        var title = System.Net.WebUtility.HtmlEncode(deck.Title);
        var antiforgery = $"<input type=\"hidden\" name=\"{tokens.FormFieldName}\" value=\"{System.Net.WebUtility.HtmlEncode(tokens.RequestToken)}\">";
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex">
            <title>Passcode · {{title}}</title><link rel="stylesheet" href="/css/podium.css"></head>
            <body class="centered"><main class="card" style="max-width:26rem; margin:10vh auto; padding:1.5rem">
            <h1 style="font-size:1.2rem; margin:0 0 .4rem">{{title}}</h1>
            <p class="muted" style="margin:0 0 1rem">This link is protected with a passcode.</p>
            {{(wrong ? "<p class=\"error\" role=\"alert\">That passcode is not right.</p>" : "")}}
            <form method="post" action="/d/{{deck.Slug}}/unlock">
              <input type="hidden" name="share" value="{{System.Net.WebUtility.HtmlEncode(share)}}">
              <input type="hidden" name="next" value="{{System.Net.WebUtility.HtmlEncode(next)}}">
              {{antiforgery}}
              <label for="passcode" class="faint">Passcode</label>
              <input class="input" id="passcode" name="passcode" type="password" autocomplete="one-time-code" autofocus required maxlength="200" style="width:100%; margin:.3rem 0 .8rem">
              <button class="btn btn-primary" type="submit">Open deck</button>
            </form></main></body></html>
            """;
    }

    private static string RequestAccessPage(string slug, Caller caller, AccessRequestStatus? status, AntiforgeryTokenSet tokens)
    {
        var who = System.Net.WebUtility.HtmlEncode(caller.DisplayName ?? caller.Principal ?? "");
        var antiforgery = $"<input type=\"hidden\" name=\"{tokens.FormFieldName}\" value=\"{System.Net.WebUtility.HtmlEncode(tokens.RequestToken)}\">";
        var body = status switch
        {
            AccessRequestStatus.Pending => "<p class=\"muted\">Your request is waiting for the owner. You will be able to open the deck once it is approved.</p>",
            AccessRequestStatus.Declined => "<p class=\"muted\">Your request was declined.</p>",
            AccessRequestStatus.Granted => "<p class=\"muted\">Access was granted; <a href=\"/d/" + slug + "/\">open the deck</a>.</p>",
            _ => $$"""
                <p class="muted">If this deck exists and is shared on request, the owner will see who asked.</p>
                <form method="post" action="/d/{{slug}}/request-access">
                  {{antiforgery}}
                  <label for="message" class="faint">Message (optional)</label>
                  <textarea class="input" id="message" name="message" rows="3" maxlength="500" style="width:100%; margin:.3rem 0 .8rem" placeholder="Hi, I attended your talk at ..."></textarea>
                  <button class="btn btn-primary" type="submit">Request access</button>
                </form>
                """,
        };
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex">
            <title>Not available</title><link rel="stylesheet" href="/css/podium.css"></head>
            <body class="centered"><main class="card" style="max-width:28rem; margin:10vh auto; padding:1.5rem">
            <h1 style="font-size:1.2rem; margin:0 0 .4rem">This deck is not available to you</h1>
            <p class="faint" style="margin:0 0 1rem">Signed in as {{who}}.</p>
            {{body}}
            <p style="margin-top:1rem"><a href="/shared">Decks shared with me</a></p>
            </main></body></html>
            """;
    }

    private static string RemotePage(Deck deck)
    {
        var title = System.Net.WebUtility.HtmlEncode(deck.Title);
        var slug = deck.Slug;
        var hasNotes = deck.CurrentHasNotes ? "1" : "0";
        var hasSheet = deck.CurrentHasSlideSheet ? "1" : "0";
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover,user-scalable=no">
            <meta name="theme-color" content="#0b0d12"><title>Remote · {{title}}</title>
            <link rel="stylesheet" href="/css/remote.css">
            </head><body data-slug="{{slug}}" data-has-notes="{{hasNotes}}" data-has-sheet="{{hasSheet}}" data-build="{{deck.CurrentBuildId}}">
            <header>
              <div class="title">{{title}}</div>
              <div class="status"><span class="presence" id="presence" title="People watching"></span><span class="conn" id="conn" aria-live="polite">connecting…</span></div>
            </header>
            <main>
              <div class="counter"><span id="page">–</span><span class="of">/ <span id="total">–</span></span><span class="clicks" id="clicks"></span></div>
              <section class="notes" id="notes" hidden>
                <div class="note-current" id="note-current"></div>
                <div class="note-next"><span class="label">Next</span> <span id="note-next-title"></span><div id="note-next"></div></div>
              </section>
              <div class="pad">
                <button class="nav prev" id="prev" aria-label="Previous"><svg viewBox="0 0 24 24" width="40" height="40" fill="none" stroke="currentColor" stroke-width="2.5"><path d="m15 5-7 7 7 7"/></svg></button>
                <button class="nav next" id="next" aria-label="Next"><svg viewBox="0 0 24 24" width="40" height="40" fill="none" stroke="currentColor" stroke-width="2.5"><path d="m9 5 7 7-7 7"/></svg></button>
              </div>
              <div class="row">
                <button class="small" id="first">⇤ First</button>
                <button class="small" id="goto" hidden>⊞ Go to</button>
                <button class="small" id="black" aria-pressed="false">■ Black</button>
                <button class="small" id="message">💬 Message</button>
              </div>
              <div class="row">
                <button class="small" id="timer-toggle">▶ Timer</button>
                <button class="small" id="timer-reset" title="Reset timer">↺</button>
                <span class="timer" id="timer">00:00</span>
                <span class="countdown" id="countdown" hidden></span>
                <button class="small" id="plan" title="Planned duration">⏱ Plan</button>
              </div>
              <div class="hint">Drives every open instance of this deck (audience view, projector). Keyboard: ← → Space, B = black, G = go to.</div>
            </main>
            <dialog id="goto-dialog"><div class="goto-head"><strong>Go to slide</strong><button class="small" id="goto-close">✕</button></div><div class="goto-grid" id="goto-grid"></div></dialog>
            <dialog id="message-dialog"><form method="dialog"><label>Message for the audience<input id="message-text" maxlength="300" placeholder="Demo in progress, back in a minute"></label><div class="row"><button class="small" value="show">Show</button><button class="small" value="clear">Clear</button><button class="small" value="cancel">Cancel</button></div></form></dialog>
            <dialog id="plan-dialog"><form method="dialog"><label>Planned length (minutes)<input id="plan-minutes" type="number" min="1" max="600" inputmode="numeric"></label><div class="row"><button class="small" value="set">Set</button><button class="small" value="clear">No countdown</button></div></form></dialog>
            <script src="/js/remote.js"></script>
            </body></html>
            """;
    }
}
