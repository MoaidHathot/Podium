using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Core.Security;
using Podium.Web.Security;

namespace Podium.Web.Serving;

/// <summary>
/// Serves built decks: /d/{slug}/ (SPA + assets), /d/{slug}.pdf, /d/{slug}.pptx.
/// Everything goes through <see cref="DeckAccessService"/>; nothing is ever read from storage before the access
/// decision. Private decks look like 404s to callers who are signed in but not allowed, to avoid enumeration.
/// </summary>
public static class DeckServingEndpoints
{
    private const string LiveScriptPath = "/_podium/live.js";

    public static IEndpointRouteBuilder MapDeckServing(this IEndpointRouteBuilder app)
    {
        app.MapGet("/d/{slug}.pdf", (string slug, HttpContext http, DeckAccessService access, IArtifactStore artifacts, CallerResolver callers, IViewHistoryStore views, IMemoryCache cache, CancellationToken ct)
            => ServeArtifact(slug, ArtifactKind.Pdf, http, access, artifacts, callers, views, cache, ct));
        app.MapGet("/d/{slug}.pptx", (string slug, HttpContext http, DeckAccessService access, IArtifactStore artifacts, CallerResolver callers, IViewHistoryStore views, IMemoryCache cache, CancellationToken ct)
            => ServeArtifact(slug, ArtifactKind.Pptx, http, access, artifacts, callers, views, cache, ct));
        app.MapGet("/d/{slug}.jpg", async (string slug, HttpContext http, DeckAccessService access, IArtifactStore artifacts, CallerResolver callers, CancellationToken ct) =>
        {
            var caller = callers.Resolve(http.User);
            var result = await access.EvaluateAsync(http, slug, ArtifactKind.Thumbnail, caller, ct);
            if (result.Deck is null || result.Decision != AccessDecision.Allow || result.Deck.CurrentBuildId is null) return Results.NotFound();
            var file = await artifacts.OpenArtifactAsync(slug, result.Deck.CurrentBuildId, ArtifactKind.Thumbnail, ct);
            if (file is null) return Results.NotFound();
            // The UI appends ?v=<buildId>, so long-lived caching is safe.
            http.Response.Headers[HeaderNames.CacheControl] = "private, max-age=86400";
            http.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            return Results.Stream(file.Content, "image/jpeg", lastModified: file.LastModified,
                entityTag: file.ETag is null ? null : new Microsoft.Net.Http.Headers.EntityTagHeaderValue(QuoteEtag(file.ETag)));
        });
        // Note: routing ignores trailing slashes, so "/d/{slug}" and "/d/{slug}/" both land here with an empty path.
        app.MapMethods("/d/{slug}/{**path}", ["GET", "HEAD"], ServeSite);
        return app;
    }

    private static async Task<IResult> ServeSite(string slug, string? path, HttpContext http, DeckAccessService access, IArtifactStore artifacts, CallerResolver callers, IViewHistoryStore views, IMemoryCache cache, CancellationToken ct)
    {
        path ??= "";
        // Slidev is built with base "/d/{slug}/"; relative URLs only resolve correctly from the slash-terminated form.
        if (path.Length == 0 && !http.Request.Path.Value!.EndsWith('/'))
            return Results.Redirect($"/d/{slug}/{http.Request.QueryString}", permanent: false);

        var caller = callers.Resolve(http.User);
        var result = await access.EvaluateAsync(http, slug, ArtifactKind.Site, caller, ct);
        if (result.Deck is null) return Results.NotFound();

        switch (result.Decision)
        {
            case AccessDecision.RequireLogin:
                return IsNavigation(http) ? Results.Redirect(LoginUrl(http)) : Results.Unauthorized();
            case AccessDecision.Deny:
                return Results.NotFound();
        }

        var deck = result.Deck;
        if (deck.CurrentBuildId is null)
        {
            return caller.IsOwner && IsNavigation(http)
                ? Results.Redirect($"/decks/{slug}?notBuilt=1")
                : Results.NotFound();
        }

        var isIndex = path.Length == 0;
        var relative = isIndex ? "index.html" : path;

        // Never allow path tricks; blob names are exact so ".." has no meaning there, but keep the contract explicit.
        if (relative.Contains("..", StringComparison.Ordinal) || relative.Contains('\\')) return Results.NotFound();

        var file = await artifacts.OpenSiteFileAsync(slug, deck.CurrentBuildId, relative, ct);
        if (file is null)
        {
            var lastSegment = relative[(relative.LastIndexOf('/') + 1)..];
            if (lastSegment.Contains('.')) return Results.NotFound();
            // SPA route (/5, /presenter/3, /overview, /notes ...): fall back to index.html
            isIndex = true;
            file = await artifacts.OpenSiteFileAsync(slug, deck.CurrentBuildId, "index.html", ct);
            if (file is null) return Results.NotFound();
        }

        var headers = http.Response.Headers;
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["X-Podium-Build"] = deck.CurrentBuildId;

        if (isIndex)
        {
            await using (file)
            {
                using var ms = new MemoryStream();
                await file.Content.CopyToAsync(ms, ct);
                var html = Encoding.UTF8.GetString(ms.ToArray());
                html = InjectLiveScript(html, slug, deck.CurrentBuildId);

                headers[HeaderNames.CacheControl] = "no-cache, private";
                if (!result.Trusted)
                {
                    // Untrusted (external) decks run in an opaque origin: no cookies, no credentialed same-origin fetches.
                    headers[HeaderNames.ContentSecurityPolicy] = "sandbox allow-scripts allow-popups allow-forms allow-modals allow-pointer-lock allow-downloads";
                }
                await RecordViewAsync(views, cache, caller, slug, path, ArtifactKind.Site, ct);
                return Results.Content(html, "text/html; charset=utf-8");
            }
        }

        headers[HeaderNames.CacheControl] = relative.StartsWith("assets/", StringComparison.Ordinal)
            ? "private, max-age=31536000, immutable"
            : "private, max-age=600";

        return Results.Stream(file.Content, file.ContentType, lastModified: file.LastModified,
            entityTag: file.ETag is null ? null : new Microsoft.Net.Http.Headers.EntityTagHeaderValue(QuoteEtag(file.ETag)),
            enableRangeProcessing: true);
    }

    private static async Task<IResult> ServeArtifact(string slug, ArtifactKind kind, HttpContext http, DeckAccessService access, IArtifactStore artifacts, CallerResolver callers, IViewHistoryStore views, IMemoryCache cache, CancellationToken ct)
    {
        var caller = callers.Resolve(http.User);
        var result = await access.EvaluateAsync(http, slug, kind, caller, ct);
        if (result.Deck is null) return Results.NotFound();
        switch (result.Decision)
        {
            case AccessDecision.RequireLogin: return Results.Redirect(LoginUrl(http));
            case AccessDecision.Deny: return Results.NotFound();
        }
        if (result.Deck.CurrentBuildId is null) return Results.NotFound();

        var file = await artifacts.OpenArtifactAsync(slug, result.Deck.CurrentBuildId, kind, ct);
        if (file is null) return Results.NotFound();

        http.Response.Headers[HeaderNames.CacheControl] = "private, no-cache";
        http.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        var fileName = $"{slug}.{kind.ToString().ToLowerInvariant()}";
        var inline = kind == ArtifactKind.Pdf && !http.Request.Query.ContainsKey("download");
        http.Response.Headers[HeaderNames.ContentDisposition] = $"{(inline ? "inline" : "attachment")}; filename=\"{fileName}\"";
        await RecordViewAsync(views, cache, caller, slug, "", kind, ct);
        return Results.Stream(file.Content, file.ContentType, lastModified: file.LastModified,
            entityTag: file.ETag is null ? null : new Microsoft.Net.Http.Headers.EntityTagHeaderValue(QuoteEtag(file.ETag)),
            enableRangeProcessing: true);
    }

    /// <summary>Adds the Podium live script (new-build detection + presenter sync bootstrap) before &lt;/head&gt;.</summary>
    internal static string InjectLiveScript(string html, string slug, string buildId)
    {
        var tag = $"<script defer src=\"{LiveScriptPath}\" data-slug=\"{slug}\" data-build=\"{buildId}\"></script>";
        var idx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? tag + html : html.Insert(idx, tag);
    }

    private static async Task RecordViewAsync(IViewHistoryStore views, IMemoryCache cache, Caller caller, string slug, string path, ArtifactKind kind, CancellationToken ct)
    {
        var principal = caller.Principal ?? "anonymous";
        var key = $"view:{principal}:{slug}:{kind}";
        if (cache.TryGetValue(key, out _)) return;
        cache.Set(key, true, TimeSpan.FromMinutes(10));
        try
        {
            await views.RecordAsync(new ViewEvent { DeckSlug = slug, Principal = principal, Artifact = kind, Presenter = path.StartsWith("presenter", StringComparison.OrdinalIgnoreCase) }, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // View history is best-effort; never fail a page because of it.
        }
    }

    private static bool IsNavigation(HttpContext http)
    {
        var accept = http.Request.Headers.Accept.ToString();
        var dest = http.Request.Headers["Sec-Fetch-Dest"].ToString();
        return dest is "document" or "iframe" || accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static string LoginUrl(HttpContext http) => "/login?returnUrl=" + Uri.EscapeDataString(http.Request.Path + http.Request.QueryString);

    private static string QuoteEtag(string etag) => etag.StartsWith('"') || etag.StartsWith("W/", StringComparison.Ordinal) ? etag : $"\"{etag}\"";
}
