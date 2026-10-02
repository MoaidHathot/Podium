using Microsoft.Extensions.Caching.Memory;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Core.Security;

namespace Podium.Web.Serving;

public sealed record DeckAccessResult(Deck? Deck, AccessDecision Decision, bool ViaShareLink, bool Trusted, bool CanPresent = false);

/// <summary>
/// Resolves a deck and decides access for the current request. Share links arrive as ?share=ID and are then carried
/// in a host-only, path-scoped cookie so the SPA's asset requests keep working.
/// </summary>
public sealed class DeckAccessService(IDeckStore decks, ISourceStore sources, IGrantStore grants, IShareLinkStore links, IMemoryCache cache)
{
    private static readonly TimeSpan DeckCacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SourceCacheTtl = TimeSpan.FromSeconds(60);

    public static string ShareCookieName(string slug) => "podium_share_" + slug;

    public async Task<Deck?> GetDeckAsync(string slug, CancellationToken ct)
    {
        if (!Podium.Core.Slug.IsValid(slug)) return null;
        return await cache.GetOrCreateAsync("deck:" + slug, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = DeckCacheTtl;
            return await decks.GetAsync(slug, ct);
        });
    }

    public async Task<Source?> GetSourceAsync(string sourceId, CancellationToken ct)
        => await cache.GetOrCreateAsync("source:" + sourceId, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = SourceCacheTtl;
            return await sources.GetAsync(sourceId, ct);
        });

    public void Invalidate(string slug) => cache.Remove("deck:" + slug);
    public void InvalidateSource(string sourceId) => cache.Remove("source:" + sourceId);

    public async Task<DeckAccessResult> EvaluateAsync(HttpContext http, string slug, ArtifactKind artifact, Caller caller, CancellationToken ct)
    {
        var deck = await GetDeckAsync(slug, ct);
        if (deck is null) return new DeckAccessResult(null, AccessDecision.Deny, false, false, false);

        var viaLink = false;
        // On the external origin a view token may carry a link grant for exactly one deck.
        if (!caller.IsOwner && http.Items.TryGetValue("podium.viewLinkSlug", out var granted) && granted is string gs && gs == slug)
            viaLink = true;
        if (!caller.IsOwner && !viaLink)
        {
            var linkId = http.Request.Query["share"].FirstOrDefault() ?? http.Request.Cookies[ShareCookieName(slug)];
            if (!string.IsNullOrEmpty(linkId) && linkId.Length <= 64)
            {
                var link = await links.GetAsync(linkId, ct);
                // A link for the deck itself also covers its thumbnail.
                var linkArtifact = artifact == ArtifactKind.Thumbnail ? ArtifactKind.Site : artifact;
                viaLink = link is not null && !link.Revoked && link.DeckSlug == slug && link.Artifact == linkArtifact
                          && (link.ExpiresAt is null || link.ExpiresAt > DateTimeOffset.UtcNow);
                if (viaLink && http.Request.Query.ContainsKey("share"))
                {
                    // Host-wide path: the deck's sync socket (/ws/sync/{slug}) and version probe (/api/decks/{slug}/version)
                    // live outside /d/{slug}/, and the cookie is already bound to one deck by name and validated per slug.
                    http.Response.Cookies.Append(ShareCookieName(slug), linkId, new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = http.Request.IsHttps,
                        SameSite = SameSiteMode.Lax,
                        Path = "/",
                        MaxAge = link!.ExpiresAt is { } exp ? exp - DateTimeOffset.UtcNow : TimeSpan.FromDays(7),
                    });
                }
            }
        }

        IReadOnlyCollection<Grant> deckGrants = [];
        if (!caller.IsOwner && caller.IsAuthenticated)
            deckGrants = await grants.ListForDeckAsync(slug, ct);

        var decision = AccessPolicy.Evaluate(deck, artifact, caller, deckGrants, viaLink);
        var trusted = decision == AccessDecision.Allow && (await GetSourceAsync(deck.SourceId, ct))?.Trusted == true;
        // Presenting (notes, driving the sync) is for the owner and grantees explicitly given the right; never via a link.
        var canPresent = caller.IsOwner || (caller.IsAuthenticated && deckGrants.Any(g => g.Principal == caller.Principal && g.Present));
        return new DeckAccessResult(deck, decision, viaLink, trusted, canPresent);
    }
}
