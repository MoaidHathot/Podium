using Podium.Core.Models;

namespace Podium.Core.Security;

/// <summary>Identity of the caller, resolved by the web layer.</summary>
public sealed record Caller(string? Principal, bool IsOwner, string? DisplayName)
{
    public static readonly Caller Anonymous = new(null, false, null);
    public bool IsAuthenticated => Principal is not null;
}

public enum AccessDecision
{
    Allow,
    /// <summary>Caller is anonymous and the resource requires sign-in.</summary>
    RequireLogin,
    Deny,
}

/// <summary>
/// Single place where all access rules live. The web layer never inspects Visibility directly.
/// </summary>
public static class AccessPolicy
{
    public static AccessDecision Evaluate(Deck deck, ArtifactKind artifact, Caller caller, IReadOnlyCollection<Grant> grants, bool validShareLink)
    {
        if (deck.Archived && !caller.IsOwner) return AccessDecision.Deny;
        if (caller.IsOwner) return AccessDecision.Allow;

        // Only the deliverables can ever be exposed; logs and anything else are owner-only. Thumbnails follow the site.
        if (artifact is not (ArtifactKind.Site or ArtifactKind.Pdf or ArtifactKind.Pptx or ArtifactKind.Thumbnail)) return AccessDecision.Deny;

        var visibility = artifact switch
        {
            ArtifactKind.Pdf => deck.PdfVisibility,
            ArtifactKind.Pptx => deck.PptxVisibility,
            _ => deck.Visibility,
        };

        switch (visibility)
        {
            case Visibility.Public:
                return AccessDecision.Allow;
            case Visibility.Link:
                return validShareLink ? AccessDecision.Allow : (caller.IsAuthenticated ? AccessDecision.Deny : AccessDecision.RequireLogin);
            case Visibility.Shared:
                if (validShareLink) return AccessDecision.Allow;
                if (!caller.IsAuthenticated) return AccessDecision.RequireLogin;
                var g = grants.FirstOrDefault(x => string.Equals(x.Principal, caller.Principal, StringComparison.Ordinal));
                if (g is null) return AccessDecision.Deny;
                var ok = artifact switch
                {
                    ArtifactKind.Site => g.Site,
                    ArtifactKind.Pdf => g.Pdf,
                    ArtifactKind.Pptx => g.Pptx,
                    _ => false,
                };
                return ok ? AccessDecision.Allow : AccessDecision.Deny;
            case Visibility.Private:
            default:
                // A valid share link created by the owner is honoured even for Private decks; the owner controls its lifetime.
                if (validShareLink) return AccessDecision.Allow;
                return caller.IsAuthenticated ? AccessDecision.Deny : AccessDecision.RequireLogin;
        }
    }
}
