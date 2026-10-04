using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Web.Pages.Decks;

public sealed class DetailsModel(IDeckStore decks, ISourceStore sources, IBuildStore builds, IGrantStore grants, IShareLinkStore links, IViewHistoryStore views, ISessionStore sessions, IAccessRequestStore accessRequests) : PageModel
{
    public IReadOnlyList<AccessRequest> PendingRequests { get; private set; } = [];
    public IReadOnlyList<Session> Sessions { get; private set; } = [];
    public Session? LiveSession => Sessions.FirstOrDefault(s => s.EndedAt is null && s.Id == Deck.LiveSessionId);
    public IReadOnlyList<ViewEvent> RecentViews { get; private set; } = [];
    public int Views7d { get; private set; }
    public int Views30d { get; private set; }
    public int DistinctViewers30d { get; private set; }
    public Deck Deck { get; private set; } = default!;
    public Source? Source { get; private set; }
    public IReadOnlyList<Build> Builds { get; private set; } = [];
    public IReadOnlyList<Grant> Grants { get; private set; } = [];
    public IReadOnlyList<ShareLink> Links { get; private set; } = [];
    public Build? CurrentBuild { get; private set; }
    [BindProperty(SupportsGet = true)] public bool NotBuilt { get; set; }
    public string Origin => $"{Request.Scheme}://{Request.Host}";

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        var deck = await decks.GetAsync(slug, ct);
        if (deck is null) return NotFound();
        Deck = deck;
        Source = await sources.GetAsync(deck.SourceId, ct);
        Builds = await builds.ListForDeckAsync(slug, 15, ct);
        Grants = await grants.ListForDeckAsync(slug, ct);
        Links = await links.ListForDeckAsync(slug, ct);
        CurrentBuild = deck.CurrentBuildId is null ? null : Builds.FirstOrDefault(b => b.Id == deck.CurrentBuildId) ?? await builds.GetAsync(slug, deck.CurrentBuildId, ct);
        RecentViews = await views.RecentForDeckAsync(slug, 200, ct);
        Sessions = await sessions.ListForDeckAsync(slug, 10, ct);
        PendingRequests = (await accessRequests.ListForDeckAsync(slug, ct)).Where(r => r.Status == AccessRequestStatus.Pending).ToList();
        var now = DateTimeOffset.UtcNow;
        Views7d = RecentViews.Count(v => v.At > now.AddDays(-7));
        Views30d = RecentViews.Count(v => v.At > now.AddDays(-30));
        DistinctViewers30d = RecentViews.Where(v => v.At > now.AddDays(-30)).Select(v => v.Principal).Distinct().Count();
        return Page();
    }
}
