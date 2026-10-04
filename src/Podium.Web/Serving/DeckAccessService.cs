using Microsoft.Extensions.Caching.Memory;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Core.Security;

namespace Podium.Web.Serving;

public sealed record DeckAccessResult(Deck? Deck, AccessDecision Decision, bool ViaShareLink, bool Trusted, bool CanPresent = false, string? LinkId = null, string? NeedsPasscodeFor = null);

/// <summary>
/// Resolves a deck and decides access for the current request. Share links arrive as ?share=ID; once admitted, the
/// browser carries a signed, host-wide cookie ("linkId.exp.sig") so the SPA's asset requests, the sync socket and the
/// version probe keep working. Admission is where limits apply: a link's opens are counted then, a max-uses cap is
/// enforced then, and a passcode (if the link has one) must have been entered. A revoked or expired link stops working
/// for cookie holders too, since the link record is checked on every request.
/// </summary>
public sealed class DeckAccessService(IDeckStore decks, ISourceStore sources, IGrantStore grants, IShareLinkStore links, IMemoryCache cache, Security.HmacTokenService hmac)
{
    private static readonly TimeSpan DeckCacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SourceCacheTtl = TimeSpan.FromSeconds(60);
    private const string CookiePurpose = "share-cookie";

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

    public async Task<Deck?> GetDeckByAliasAsync(string alias, CancellationToken ct)
        => await cache.GetOrCreateAsync("alias:" + alias, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = DeckCacheTtl;
            return await decks.GetByAliasAsync(alias, ct);
        });

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
        string? linkId = null;
        string? needsPasscode = null;
        // On the external origin a view token may carry a link grant for exactly one deck.
        if (!caller.IsOwner && http.Items.TryGetValue("podium.viewLinkSlug", out var granted) && granted is string gs && gs == slug)
            viaLink = true;
        if (!caller.IsOwner && !viaLink)
        {
            // A link for the deck itself also covers its thumbnail.
            var linkArtifact = artifact == ArtifactKind.Thumbnail ? ArtifactKind.Site : artifact;

            // 1. Already admitted? The signed cookie names the link; the link itself must still be alive.
            var cookie = http.Request.Cookies[ShareCookieName(slug)];
            if (ParseCookie(cookie) is { } admittedId)
            {
                var link = await links.GetAsync(admittedId, ct);
                if (IsAlive(link, slug, linkArtifact)) { viaLink = true; linkId = admittedId; }
            }

            // 2. Fresh arrival with ?share=: admit (count, cap, passcode) and set the cookie.
            var shareId = http.Request.Query["share"].FirstOrDefault();
            if (!viaLink && !string.IsNullOrEmpty(shareId) && shareId.Length <= 64 && shareId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            {
                var link = await links.GetAsync(shareId, ct);
                if (IsAlive(link, slug, linkArtifact))
                {
                    if (link!.MaxUses is { } max && link.Opens >= max)
                    {
                        // Exhausted: nothing to admit; the policy falls through to the deck's own visibility.
                    }
                    else if (link.PasscodeHash is not null && !PasscodeAccepted(http, link))
                    {
                        needsPasscode = link.Id;
                    }
                    else
                    {
                        viaLink = true;
                        linkId = link.Id;
                        await AdmitAsync(http, link, ct);
                    }
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
        return new DeckAccessResult(deck, decision, viaLink, trusted, canPresent, linkId, decision == AccessDecision.Allow ? null : needsPasscode);
    }

    /// <summary>Called by the unlock page after a correct passcode: admits the browser exactly like a plain ?share= arrival.</summary>
    public async Task<bool> AdmitWithPasscodeAsync(HttpContext http, string slug, string shareId, string passcode, CancellationToken ct)
    {
        var link = await links.GetAsync(shareId, ct);
        if (!IsAlive(link, slug, ArtifactKind.Site) && !IsAlive(link, slug, ArtifactKind.Pdf) && !IsAlive(link, slug, ArtifactKind.Pptx)) return false;
        if (link!.PasscodeHash is null || !Security.Passcodes.Verify(passcode, link.PasscodeHash)) return false;
        if (link.MaxUses is { } max && link.Opens >= max) return false;
        await AdmitAsync(http, link, ct);
        return true;
    }

    private static bool IsAlive(ShareLink? link, string slug, ArtifactKind artifact)
        => link is not null && !link.Revoked && link.DeckSlug == slug && link.Artifact == artifact
           && (link.ExpiresAt is null || link.ExpiresAt > DateTimeOffset.UtcNow);

    // A passcode entered on the unlock page is proven by the signed cookie it produced; a fresh ?share= arrival on
    // another browser must enter it again.
    private bool PasscodeAccepted(HttpContext http, ShareLink link) => ParseCookie(http.Request.Cookies[ShareCookieName(link.DeckSlug)]) == link.Id;

    private async Task AdmitAsync(HttpContext http, ShareLink link, CancellationToken ct)
    {
        var lifetime = link.ExpiresAt is { } exp ? exp - DateTimeOffset.UtcNow : TimeSpan.FromDays(7);
        if (lifetime <= TimeSpan.Zero) return;
        var expUnix = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        var body = $"{link.Id}.{expUnix}";
        var value = $"{body}.{hmac.SignFor(CookiePurpose, body)}";
        // Host-wide path: the deck's sync socket (/ws/sync/{slug}) and version probe (/api/decks/{slug}/version)
        // live outside /d/{slug}/, and the cookie is already bound to one deck by name and validated per slug.
        http.Response.Cookies.Append(ShareCookieName(link.DeckSlug), value, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = lifetime,
        });
        // Best effort usage tracking (a lost increment never blocks a viewer).
        try
        {
            var fresh = await links.GetAsync(link.Id, ct) ?? link;
            await links.UpsertAsync(fresh with { Opens = fresh.Opens + 1, LastOpenedAt = DateTimeOffset.UtcNow }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _ = ex; }
    }

    /// <summary>Returns the link id from a valid signed cookie, or null. Legacy unsigned cookies (plain id) are not honoured.</summary>
    private string? ParseCookie(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie) || cookie.Length > 200) return null;
        var parts = cookie.Split('.');
        if (parts.Length != 3) return null;
        if (!long.TryParse(parts[1], out var exp) || DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow) return null;
        return hmac.VerifyFor(CookiePurpose, $"{parts[0]}.{parts[1]}", parts[2]) ? parts[0] : null;
    }
}