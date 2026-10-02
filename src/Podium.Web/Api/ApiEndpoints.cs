using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Core.Security;
using Podium.Core.Services;
using Podium.Web.GitHub;
using Podium.Web.Security;
using Podium.Web.Serving;

namespace Podium.Web.Api;

public static class ApiEndpoints
{
    /// <summary>Mutating owner endpoints require this header: browsers cannot add it cross-origin without a CORS preflight, which we never grant.</summary>
    public const string RequestHeader = "X-Podium-Request";

    public static IEndpointRouteBuilder MapPodiumApi(this IEndpointRouteBuilder app)
    {
        // ----- Unauthenticated, token/signature protected -----
        app.MapPost("/api/builds/{slug}/{buildId}/report", async (string slug, string buildId, HttpContext http, BuildService builds, DeckAccessService access, CancellationToken ct) =>
        {
            var auth = http.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)) return Results.Unauthorized();
            if (!Podium.Core.Slug.IsValid(slug) || buildId.Length > 40) return Results.BadRequest();
            var report = await http.Request.ReadFromJsonAsync<BuildReport>(ct);
            if (report is null) return Results.BadRequest();
            var ok = await builds.CompleteAsync(slug, buildId, auth[7..], report, ct);
            if (ok) access.Invalidate(slug);
            return ok ? Results.Ok() : Results.Unauthorized();
        }).DisableAntiforgery();

        app.MapPost("/api/github/webhook", async (HttpContext http, GitHubWebhookHandler handler, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("Webhook");
            http.Request.EnableBuffering();
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            if (ms.Length > 2 * 1024 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (!handler.VerifySignature(ms.GetBuffer().AsSpan(0, (int)ms.Length), http.Request.Headers["X-Hub-Signature-256"]))
            {
                log.LogWarning("Webhook signature verification failed from {Ip}", http.Connection.RemoteIpAddress);
                return Results.Unauthorized();
            }
            var eventName = http.Request.Headers["X-GitHub-Event"].ToString();
            var payload = ms.ToArray();
            // GitHub gives up after 10 seconds and a scaled-to-zero app may already have spent most of that starting up.
            // Acknowledge now; the handler only talks to GitHub's API and enqueues work, so nothing is lost by detaching.
            handler.HandleInBackground(eventName, payload);
            return Results.Accepted(value: new { outcome = "accepted" });
        }).DisableAntiforgery();

        // ----- Access-policy protected (works for anonymous public decks) -----
        app.MapGet("/api/decks/{slug}/version", async (string slug, HttpContext http, DeckAccessService access, CallerResolver callers, CancellationToken ct) =>
        {
            var caller = callers.Resolve(http.User);
            var r = await access.EvaluateAsync(http, slug, ArtifactKind.Site, caller, ct);
            if (r.Deck is null || r.Decision != AccessDecision.Allow) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { build = r.Deck.CurrentBuildId, status = r.Deck.LatestBuildStatus?.ToString(), updatedAt = r.Deck.UpdatedAt });
        });

        // ----- Owner only -----
        var owner = app.MapGroup("/api").RequireAuthorization(PodiumClaims.OwnerPolicy).AddEndpointFilter<RequestHeaderFilter>();

        owner.MapGet("/decks", async (IDeckStore decks, IBuildStore builds, CancellationToken ct) =>
        {
            var list = await decks.ListAsync(includeArchived: true, ct);
            return Results.Ok(list);
        });

        owner.MapGet("/decks/{slug}", async (string slug, IDeckStore decks, IBuildStore builds, IGrantStore grants, IShareLinkStore links, CancellationToken ct) =>
        {
            var deck = await decks.GetAsync(slug, ct);
            if (deck is null) return Results.NotFound();
            return Results.Ok(new
            {
                deck,
                builds = await builds.ListForDeckAsync(slug, 10, ct),
                grants = await grants.ListForDeckAsync(slug, ct),
                links = await links.ListForDeckAsync(slug, ct),
            });
        });

        owner.MapPatch("/decks/{slug}", async (string slug, DeckPatch patch, IDeckStore decks, DeckAccessService access, CancellationToken ct) =>
        {
            var deck = await decks.GetAsync(slug, ct);
            if (deck is null) return Results.NotFound();
            deck = deck with
            {
                Visibility = patch.Visibility ?? deck.Visibility,
                PdfVisibility = patch.PdfVisibility ?? deck.PdfVisibility,
                PptxVisibility = patch.PptxVisibility ?? deck.PptxVisibility,
                Pinned = patch.Pinned ?? deck.Pinned,
                ExportPdf = patch.ExportPdf ?? deck.ExportPdf,
                ExportPptx = patch.ExportPptx ?? deck.ExportPptx,
                PptxViewer = patch.PptxViewer ?? deck.PptxViewer,
                Title = string.IsNullOrWhiteSpace(patch.Title) ? deck.Title : patch.Title.Trim(),
                Tags = patch.Tags ?? deck.Tags,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await decks.UpsertAsync(deck, ct);
            access.Invalidate(slug);
            return Results.Ok(deck);
        });

        owner.MapPost("/decks/{slug}/rebuild", async (string slug, IDeckStore decks, ISourceStore sources, IRepositoryClient repos, BuildService builds, DeckAccessService access, CancellationToken ct) =>
        {
            var deck = await decks.GetAsync(slug, ct);
            if (deck is null) return Results.NotFound();
            var source = await sources.GetAsync(deck.SourceId, ct);
            if (source is null) return Results.Problem("Source no longer exists", statusCode: 409);
            var (sha, _) = await repos.GetHeadAsync(source, ct);
            var build = await builds.QueueAsync(deck, source, sha, "manual", [], ct, supersedeActive: true);
            access.Invalidate(slug);
            return Results.Ok(build);
        });

        owner.MapGet("/decks/{slug}/builds/{buildId}/log", async (string slug, string buildId, IArtifactStore artifacts, CancellationToken ct) =>
        {
            if (!Podium.Core.Slug.IsValid(slug) || buildId.Length > 40) return Results.BadRequest();
            var log = await artifacts.OpenArtifactAsync(slug, buildId, ArtifactKind.Log, ct);
            return log is null ? Results.NotFound() : Results.Stream(log.Content, "text/plain; charset=utf-8");
        });

        owner.MapPost("/decks/{slug}/grants", async (string slug, GrantRequest req, IDeckStore decks, IGrantStore grants, GitHubAppAuth gh, ISourceStore sources, CancellationToken ct) =>
        {
            var deck = await decks.GetAsync(slug, ct);
            if (deck is null) return Results.NotFound();
            var login = req.Login.Trim().TrimStart('@');
            if (login.Length is 0 or > 39) return Results.BadRequest(new { error = "Invalid GitHub login" });
            var any = (await sources.ListAsync(ct)).FirstOrDefault(s => s.InstallationId is not null)?.InstallationId;
            var client = await gh.CreatePublicClientAsync(any, ct);
            Octokit.User user;
            try { user = await client.User.Get(login).WaitAsync(ct); }
            catch (Octokit.NotFoundException) { return Results.NotFound(new { error = $"GitHub user '{login}' not found" }); }
            var grant = new Grant { DeckSlug = slug, Principal = $"github:{user.Id}", DisplayName = user.Login, Site = req.Site, Pdf = req.Pdf, Pptx = req.Pptx };
            await grants.UpsertAsync(grant, ct);
            return Results.Ok(grant);
        });

        owner.MapDelete("/decks/{slug}/grants/{principal}", async (string slug, string principal, IGrantStore grants, CancellationToken ct) =>
        {
            await grants.DeleteAsync(slug, principal, ct);
            return Results.NoContent();
        });

        owner.MapPost("/decks/{slug}/links", async (string slug, ShareLinkRequest req, IDeckStore decks, IShareLinkStore links, HttpContext http, CancellationToken ct) =>
        {
            var deck = await decks.GetAsync(slug, ct);
            if (deck is null) return Results.NotFound();
            if (req.Artifact is not (ArtifactKind.Site or ArtifactKind.Pdf or ArtifactKind.Pptx)) return Results.BadRequest();
            var id = NewLinkId();
            var link = new ShareLink
            {
                Id = id,
                DeckSlug = slug,
                Artifact = req.Artifact,
                Label = req.Label?.Trim(),
                ExpiresAt = req.ExpiresInDays is > 0 and <= 3650 ? DateTimeOffset.UtcNow.AddDays(req.ExpiresInDays.Value) : null,
            };
            await links.UpsertAsync(link, ct);
            var path = req.Artifact == ArtifactKind.Site ? $"/d/{slug}/" : $"/d/{slug}.{req.Artifact.ToString().ToLowerInvariant()}";
            return Results.Ok(new { link, url = $"{http.Request.Scheme}://{http.Request.Host}{path}?share={id}" });
        });

        owner.MapPost("/decks/{slug}/links/{id}/revoke", async (string slug, string id, IShareLinkStore links, CancellationToken ct) =>
        {
            var link = await links.GetAsync(id, ct);
            if (link is null || link.DeckSlug != slug) return Results.NotFound();
            await links.UpsertAsync(link with { Revoked = true }, ct);
            return Results.NoContent();
        });

        owner.MapGet("/sources", async (ISourceStore sources, CancellationToken ct) => Results.Ok(await sources.ListAsync(ct)));

        owner.MapPost("/sources", async (SourceRequest req, ISourceStore sources, IRepositoryClient repos, SyncQueue queue, GitHubAppAuth gh, Microsoft.Extensions.Options.IOptions<Configuration.PodiumOptions> opts, CancellationToken ct) =>
        {
            var owner_ = req.Owner.Trim();
            var repo = req.Repo.Trim();
            if (!IsValidGitHubName(owner_) || !IsValidGitHubName(repo)) return Results.BadRequest(new { error = "Invalid owner or repository name" });
            var id = Source.MakeId(owner_, repo);
            if (await sources.GetAsync(id, ct) is not null) return Results.Conflict(new { error = "Source already registered" });
            if (!opts.Value.AllowExternalSources) return Results.BadRequest(new { error = "External sources are disabled" });

            (bool IsPrivate, string DefaultBranch, bool CallerIsOwner) info;
            try { info = await repos.GetRepoInfoAsync(owner_, repo, ct); }
            catch (Octokit.NotFoundException) { return Results.NotFound(new { error = "Repository not found or not accessible. Install the GitHub App on it for private repositories." }); }
            // Without an App installation the only credential that can reach a private repo is the development PAT.
            if (info.IsPrivate && gh.GetDevToken() is null) return Results.BadRequest(new { error = "Private repositories must be added by installing the GitHub App on them." });

            var source = new Source { Id = id, Owner = owner_, Repo = repo, Ref = string.IsNullOrWhiteSpace(req.Ref) ? null : req.Ref.Trim(), Trusted = info.CallerIsOwner, IsPrivateRepo = info.IsPrivate };
            await sources.UpsertAsync(source, ct);
            queue.TryEnqueue(new SyncJob(id, null, false, "added"));
            return Results.Ok(source);
        });

        owner.MapDelete("/sources/{sourceOwner}/{sourceRepo}", async (string sourceOwner, string sourceRepo, ISourceStore sources, IDeckStore decks, DeckAccessService access, CancellationToken ct) =>
        {
            var id = Source.MakeId(sourceOwner, sourceRepo);
            var source = await sources.GetAsync(id, ct);
            if (source is null) return Results.NotFound();
            foreach (var d in await decks.ListBySourceAsync(id, ct))
            {
                await decks.UpsertAsync(d with { Archived = true, UpdatedAt = DateTimeOffset.UtcNow }, ct);
                access.Invalidate(d.Slug);
            }
            await sources.DeleteAsync(id, ct);
            access.InvalidateSource(id);
            return Results.NoContent();
        });

        owner.MapPost("/sources/{sourceOwner}/{sourceRepo}/sync", async (string sourceOwner, string sourceRepo, [FromQuery] bool force, ISourceStore sources, SyncQueue queue, CancellationToken ct) =>
        {
            var id = Source.MakeId(sourceOwner, sourceRepo);
            if (await sources.GetAsync(id, ct) is null) return Results.NotFound();
            queue.TryEnqueue(new SyncJob(id, null, force, "manual"));
            return Results.Accepted();
        });

        owner.MapPost("/installations/refresh", async (InstallationDiscovery discovery, CancellationToken ct) => Results.Ok(new { added = await discovery.DiscoverAsync(ct) }));

        owner.MapGet("/me", (HttpContext http, CallerResolver callers) => Results.Ok(callers.Resolve(http.User)));

        // Hide a deck from "Recently presented"; it comes back the next time it is presented.
        owner.MapPost("/recent/{slug}/dismiss", async (string slug, HttpContext http, CallerResolver callers, IViewHistoryStore views, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct) =>
        {
            if (!Podium.Core.Slug.IsValid(slug)) return Results.BadRequest();
            var caller = callers.Resolve(http.User);
            if (caller.Principal is null) return Results.Unauthorized();
            await views.DismissRecentAsync(caller.Principal, slug, DateTimeOffset.UtcNow, ct);
            // Drop the view de-duplication entry so presenting again right away records a fresh view and resurfaces the deck.
            cache.Remove($"view:{caller.Principal}:{slug}:{ArtifactKind.Site}");
            return Results.NoContent();
        });

        return app;
    }

    private static string NewLinkId()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool IsValidGitHubName(string s) => s.Length is > 0 and <= 100 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && s != "." && s != "..";
}

public sealed record DeckPatch(Visibility? Visibility, Visibility? PdfVisibility, Visibility? PptxVisibility, bool? Pinned, bool? ExportPdf, bool? ExportPptx, string? Title, IReadOnlyList<string>? Tags, PptxViewer? PptxViewer = null);
public sealed record GrantRequest(string Login, bool Site = true, bool Pdf = false, bool Pptx = false);
public sealed record ShareLinkRequest(ArtifactKind Artifact, int? ExpiresInDays, string? Label);
public sealed record SourceRequest(string Owner, string Repo, string? Ref);

/// <summary>Rejects mutating requests that lack the custom header (CSRF defence in depth on top of SameSite cookies).</summary>
public sealed class RequestHeaderFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var req = context.HttpContext.Request;
        if (!HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method) && !req.Headers.ContainsKey(ApiEndpoints.RequestHeader))
            return Results.Forbid();
        return await next(context);
    }
}
