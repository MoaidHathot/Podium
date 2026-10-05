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

public static partial class ApiEndpoints
{
    /// <summary>Mutating owner endpoints require this header: browsers cannot add it cross-origin without a CORS preflight, which we never grant.</summary>
    public const string RequestHeader = "X-Podium-Request";

    public static IEndpointRouteBuilder MapPodiumApi(this IEndpointRouteBuilder app)
    {
        // ----- Unauthenticated, token/signature protected -----
        app.MapPost("/api/builds/{slug}/{buildId}/report", async (string slug, string buildId, HttpContext http, BuildService builds, DeckAccessService access, IDeckStore decks, Sync.SyncHub hub, DeckSearchIndex search, CancellationToken ct) =>
        {
            var auth = http.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)) return Results.Unauthorized();
            if (!Podium.Core.Slug.IsValid(slug) || buildId.Length > 40) return Results.BadRequest();
            var report = await http.Request.ReadFromJsonAsync<BuildReport>(ct);
            if (report is null) return Results.BadRequest();
            var ok = await builds.CompleteAsync(slug, buildId, auth[7..], report, ct);
            if (ok)
            {
                access.Invalidate(slug);
                // Open decks learn about the new served build immediately (frozen decks keep serving the old one, so no notice).
                var deck = await decks.GetAsync(slug, ct);
                if (deck?.CurrentBuildId == buildId) await hub.NotifyBuildAsync(slug, buildId, ct);
                if (deck is not null) await search.RefreshAsync(deck, ct);
            }
            return ok ? Results.Ok() : Results.Unauthorized();
        }).DisableAntiforgery().RequireRateLimiting("webhook");

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
        }).DisableAntiforgery().RequireRateLimiting("webhook");

        // ----- Access-policy protected (works for anonymous public decks) -----
        app.MapGet("/api/decks/{slug}/version", async (string slug, HttpContext http, DeckAccessService access, CallerResolver callers, CancellationToken ct) =>
        {
            var caller = callers.Resolve(http.User);
            var r = await access.EvaluateAsync(http, slug, ArtifactKind.Site, caller, ct);
            if (r.Deck is null || r.Decision != AccessDecision.Allow) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { build = r.Deck.CurrentBuildId, status = r.Deck.LatestBuildStatus?.ToString(), updatedAt = r.Deck.UpdatedAt });
        }).RequireRateLimiting("probe");

        // ----- Owner only -----
        var owner = app.MapGroup("/api").RequireAuthorization(PodiumClaims.OwnerPolicy).AddEndpointFilter<RequestHeaderFilter>().AddEndpointFilter<AuditFilter>();

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
            string? alias = deck.Alias;
            if (patch.Alias is not null)
            {
                var wanted = patch.Alias.Trim();
                if (wanted.Length == 0) alias = null;
                else
                {
                    var normalized = Podium.Core.Slug.Normalize(wanted);
                    if (normalized.Length < 2) return Results.BadRequest(new { error = "Alias too short" });
                    if (await decks.GetAsync(normalized, ct) is not null) return Results.Conflict(new { error = "That alias is another deck''s address" });
                    var other = await decks.GetByAliasAsync(normalized, ct);
                    if (other is not null && other.Slug != slug) return Results.Conflict(new { error = "Alias already in use" });
                    alias = normalized;
                }
            }
            var tags = patch.Tags?.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 40).Distinct(StringComparer.Ordinal).Take(20).ToList();
            deck = deck with
            {
                Alias = alias,
                Visibility = patch.Visibility ?? deck.Visibility,
                PdfVisibility = patch.PdfVisibility ?? deck.PdfVisibility,
                PptxVisibility = patch.PptxVisibility ?? deck.PptxVisibility,
                Pinned = patch.Pinned ?? deck.Pinned,
                ExportPdf = patch.ExportPdf ?? deck.ExportPdf,
                ExportPptx = patch.ExportPptx ?? deck.ExportPptx,
                PptxViewer = patch.PptxViewer ?? deck.PptxViewer,
                StripNotesForViewers = patch.StripNotesForViewers ?? deck.StripNotesForViewers,
                OfflineCache = patch.OfflineCache ?? deck.OfflineCache,
                // Embedding only ever applies to Public decks; the flag is kept but ignored otherwise (see CSP/XFO).
                AllowEmbedding = patch.AllowEmbedding ?? deck.AllowEmbedding,
                Title = string.IsNullOrWhiteSpace(patch.Title) ? deck.Title : patch.Title.Trim(),
                Tags = tags ?? deck.Tags,
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

        // Serve a specific successful build (rollback / promote); optionally freeze on it.
        owner.MapPost("/decks/{slug}/builds/{buildId}/serve", async (string slug, string buildId, [FromQuery] bool freeze, BuildService builds, DeckAccessService access, Sync.SyncHub hub, DeckSearchIndex search, CancellationToken ct) =>
        {
            if (!Podium.Core.Slug.IsValid(slug) || buildId.Length > 40) return Results.BadRequest();
            var deck = await builds.ServeBuildAsync(slug, buildId, freeze, ct);
            if (deck is null) return Results.NotFound(new { error = "Build not found, not successful, or its artifacts were cleaned up" });
            access.Invalidate(slug);
            await hub.NotifyBuildAsync(slug, buildId, ct);
            await search.RefreshAsync(deck, ct);
            return Results.Ok(deck);
        });

        // Permanently delete an archived deck: artifacts, build history, grants, links and the deck record itself.
        owner.MapDelete("/decks/{slug}", async (string slug, IDeckStore decks, IGrantStore grants, IShareLinkStore links, BuildService builds, DeckAccessService access, DeckSearchIndex search, CancellationToken ct) =>
        {
            var deck = await decks.GetAsync(slug, ct);
            if (deck is null) return Results.NotFound();
            if (!deck.Archived) return Results.Conflict(new { error = "Only archived decks can be deleted. Remove the deck from the repository (or the source) first; it is archived on the next sync." });
            search.Remove(slug);
            await builds.PurgeDeckAsync(deck, ct);
            foreach (var g in await grants.ListForDeckAsync(slug, ct)) await grants.DeleteAsync(slug, g.Principal, ct);
            foreach (var l in await links.ListForDeckAsync(slug, ct)) await links.UpsertAsync(l with { Revoked = true }, ct);
            await decks.DeleteAsync(slug, ct);
            access.Invalidate(slug);
            return Results.NoContent();
        });

        // Freeze: keep serving the current build while new pushes keep building in the background. Unfreeze catches up.
        owner.MapPost("/decks/{slug}/freeze", async (string slug, [FromQuery] bool frozen, BuildService builds, DeckAccessService access, IDeckStore decks, Sync.SyncHub hub, CancellationToken ct) =>
        {
            var before = (await decks.GetAsync(slug, ct))?.CurrentBuildId;
            var deck = await builds.SetFrozenAsync(slug, frozen, ct);
            if (deck is null) return Results.NotFound();
            access.Invalidate(slug);
            if (deck.CurrentBuildId is not null && deck.CurrentBuildId != before) await hub.NotifyBuildAsync(slug, deck.CurrentBuildId, ct);
            return Results.Ok(deck);
        });

        // Live sessions: Go live (freeze + join link + recording) / End (revoke + recap).
        owner.MapPost("/decks/{slug}/sessions", async (string slug, StartSessionRequest req, SessionService sessions, DeckAccessService access, HttpContext http, CancellationToken ct) =>
        {
            var (session, error) = await sessions.StartAsync(slug, req.PlannedMinutes, req.HoldDeploys, req.Freeze ?? true, req.Title, ct);
            if (session is null) return Results.Conflict(new { error });
            access.Invalidate(slug);
            var joinUrl = session.LinkId is null ? null : $"{http.Request.Scheme}://{http.Request.Host}/d/{slug}/?share={session.LinkId}";
            return Results.Ok(new { session, joinUrl });
        });

        owner.MapPost("/decks/{slug}/sessions/end", async (string slug, [FromQuery] bool unfreeze, SessionService sessions, DeckAccessService access, CancellationToken ct) =>
        {
            var session = await sessions.EndAsync(slug, "manual", unfreeze, ct);
            if (session is null) return Results.NotFound(new { error = "No live session" });
            access.Invalidate(slug);
            return Results.Ok(session);
        });

        owner.MapPost("/decks/{slug}/sessions/plan", async (string slug, [FromQuery] int minutes, SessionService sessions, CancellationToken ct) =>
        {
            var s = await sessions.UpdatePlanAsync(slug, minutes, ct);
            return s is null ? Results.NotFound(new { error = "No live session, or minutes out of range (1-600)" }) : Results.Ok(s);
        });

        owner.MapGet("/decks/{slug}/sessions", async (string slug, ISessionStore sessions, CancellationToken ct) => Results.Ok(await sessions.ListForDeckAsync(slug, 20, ct)));

        // Audit trail (owner only): newest first, optionally for one deck.
        owner.MapGet("/activity", async (string? target, IAuditStore audit, CancellationToken ct) => Results.Ok(await audit.RecentAsync(string.IsNullOrWhiteSpace(target) ? null : target, 100, ct)));

        // Full-text search across slide text (owner only; the index lives in memory).
        owner.MapGet("/search", (string? q, DeckSearchIndex search) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Length > 200) return Results.Ok(Array.Empty<SearchHit>());
            return Results.Ok(search.Search(q, 30));
        });

        // Access requests from signed-in visitors: approve (= grant) or decline.
        owner.MapGet("/access-requests", async (IAccessRequestStore requests, CancellationToken ct) => Results.Ok(await requests.ListPendingAsync(ct)));
        owner.MapPost("/decks/{slug}/access-requests/{principal}/decide", async (string slug, string principal, AccessDecisionRequest req, IDeckStore decks, IGrantStore grants, IAccessRequestStore requests, DeckAccessService access, CancellationToken ct) =>
        {
            var deck = await decks.GetAsync(slug, ct);
            var request = await requests.GetAsync(slug, principal, ct);
            if (deck is null || request is null) return Results.NotFound();
            if (req.Grant)
            {
                await grants.UpsertAsync(new Grant { DeckSlug = slug, Principal = principal, DisplayName = request.DisplayName, Site = true, Pdf = req.Pdf, Pptx = req.Pptx, Present = req.Present }, ct);
                access.Invalidate(slug);
            }
            await requests.UpsertAsync(request with { Status = req.Grant ? AccessRequestStatus.Granted : AccessRequestStatus.Declined, DecidedAt = DateTimeOffset.UtcNow }, ct);
            return Results.Ok(new { granted = req.Grant });
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
            // Presenting implies viewing the deck.
            var grant = new Grant { DeckSlug = slug, Principal = $"github:{user.Id}", DisplayName = user.Login, Site = req.Site || req.Present, Pdf = req.Pdf, Pptx = req.Pptx, Present = req.Present };
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
            if (req.Passcode is { Length: > 200 }) return Results.BadRequest(new { error = "Passcode too long" });
            if (req.MaxUses is < 1 or > 100_000) return Results.BadRequest(new { error = "Max uses must be between 1 and 100000" });
            var id = NewLinkId();
            var link = new ShareLink
            {
                Id = id,
                DeckSlug = slug,
                Artifact = req.Artifact,
                Label = req.Label?.Trim(),
                ExpiresAt = req.ExpiresInDays is > 0 and <= 3650 ? DateTimeOffset.UtcNow.AddDays(req.ExpiresInDays.Value) : null,
                PasscodeHash = string.IsNullOrWhiteSpace(req.Passcode) ? null : Passcodes.Hash(req.Passcode.Trim()),
                MaxUses = req.MaxUses,
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

            (bool IsPrivate, string DefaultBranch, bool CallerIsOwner, long RepoId) info;
            try { info = await repos.GetRepoInfoAsync(owner_, repo, ct); }
            catch (Octokit.NotFoundException) { return Results.NotFound(new { error = "Repository not found or not accessible. Install the GitHub App on it for private repositories." }); }
            // Without an App installation the only credential that can reach a private repo is the development PAT.
            if (info.IsPrivate && gh.GetDevToken() is null) return Results.BadRequest(new { error = "Private repositories must be added by installing the GitHub App on them." });

            if ((await sources.ListAsync(ct)).FirstOrDefault(s => s.RepoId == info.RepoId) is { } same)
                return Results.Conflict(new { error = $"This repository is already registered as {same.FullName} (it was renamed on GitHub)." });
            var source = new Source { Id = id, Owner = owner_, Repo = repo, RepoId = info.RepoId, Ref = string.IsNullOrWhiteSpace(req.Ref) ? null : req.Ref.Trim(), Trusted = info.CallerIsOwner, IsPrivateRepo = info.IsPrivate };
            await sources.UpsertAsync(source, ct);
            queue.TryEnqueue(new SyncJob(id, null, false, "added"));
            return Results.Ok(source);
        });

        owner.MapDelete("/sources/{sourceOwner}/{sourceRepo}", async (string sourceOwner, string sourceRepo, ISourceStore sources, IDeckStore decks, DeckAccessService access, CancellationToken ct) =>
        {
            var source = await ResolveSourceAsync(sources, sourceOwner, sourceRepo, ct);
            if (source is null) return Results.NotFound();
            foreach (var d in await decks.ListBySourceAsync(source.Id, ct))
            {
                await decks.UpsertAsync(d with { Archived = true, UpdatedAt = DateTimeOffset.UtcNow }, ct);
                access.Invalidate(d.Slug);
            }
            await sources.DeleteAsync(source.Id, ct);
            access.InvalidateSource(source.Id);
            return Results.NoContent();
        });

        owner.MapPost("/sources/{sourceOwner}/{sourceRepo}/sync", async (string sourceOwner, string sourceRepo, [FromQuery] bool force, ISourceStore sources, SyncQueue queue, CancellationToken ct) =>
        {
            var source = await ResolveSourceAsync(sources, sourceOwner, sourceRepo, ct);
            if (source is null) return Results.NotFound();
            queue.TryEnqueue(new SyncJob(source.Id, null, force, "manual"));
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

public sealed record DeckPatch(Visibility? Visibility, Visibility? PdfVisibility, Visibility? PptxVisibility, bool? Pinned, bool? ExportPdf, bool? ExportPptx, string? Title, IReadOnlyList<string>? Tags, PptxViewer? PptxViewer = null, bool? StripNotesForViewers = null, string? Alias = null, bool? OfflineCache = null, bool? AllowEmbedding = null);
public sealed record GrantRequest(string Login, bool Site = true, bool Pdf = false, bool Pptx = false, bool Present = false);
public sealed record ShareLinkRequest(ArtifactKind Artifact, int? ExpiresInDays, string? Label, string? Passcode = null, int? MaxUses = null);
public sealed record StartSessionRequest(int? PlannedMinutes, bool HoldDeploys, bool? Freeze, string? Title);
public sealed record AccessDecisionRequest(bool Grant, bool Pdf = false, bool Pptx = false, bool Present = false);

public static partial class ApiEndpoints
{
    /// <summary>Sources are keyed by the name they were registered under; after a rename on GitHub they are addressed by their current name.</summary>
    internal static async Task<Source?> ResolveSourceAsync(ISourceStore sources, string owner, string repo, CancellationToken ct)
        => await sources.GetAsync(Source.MakeId(owner, repo), ct)
           ?? (await sources.ListAsync(ct)).FirstOrDefault(s => string.Equals(s.Owner, owner, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Repo, repo, StringComparison.OrdinalIgnoreCase));
}
public sealed record SourceRequest(string Owner, string Repo, string? Ref);

/// <summary>Rejects mutating requests that lack the custom header (CSRF defence in depth on top of SameSite cookies).</summary>
/// <summary>
/// Records every successful mutating owner API call in the audit log: action = method + route template (e.g.
/// "PATCH /api/decks/{slug}"), target = the slug when the route has one, details = a compact JSON view of the
/// request body (bounded; passcodes are never logged).
/// </summary>
public sealed class AuditFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var result = await next(context);
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)) return result;
        var status = result switch { IStatusCodeHttpResult s => s.StatusCode ?? 200, _ => 200 };
        if (status is < 200 or >= 300) return result;
        var route = http.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.RouteNameMetadata>()?.RouteName
                    ?? (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value;
        var target = http.Request.RouteValues.TryGetValue("slug", out var slug) ? slug?.ToString() : http.Request.RouteValues.TryGetValue("sourceOwner", out var so) && http.Request.RouteValues.TryGetValue("sourceRepo", out var sr) ? $"{so}/{sr}" : null;
        string? details = null;
        foreach (var arg in context.Arguments)
        {
            if (arg is null || arg is HttpContext || arg is CancellationToken || arg is string || arg.GetType().Namespace?.StartsWith("Podium.Core.Abstractions", StringComparison.Ordinal) == true) continue;
            if (arg.GetType().IsClass && arg.GetType().Namespace == "Podium.Web.Api" || arg.GetType().Name.EndsWith("Request", StringComparison.Ordinal) || arg.GetType().Name.EndsWith("Patch", StringComparison.Ordinal))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(arg, arg.GetType(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                details = System.Text.RegularExpressions.Regex.Replace(json, "\"passcode\":\"[^\"]*\"", "\"passcode\":\"***\"");
                break;
            }
        }
        if (http.Request.QueryString.HasValue) details = (details is null ? "" : details + " ") + http.Request.QueryString.Value;
        await http.RequestServices.GetRequiredService<AuditService>().RecordAsync(http, $"{http.Request.Method} {route}", target, details);
        return result;
    }
}

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
