using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Web.Pages.Decks;

public sealed class DetailsModel(IDeckStore decks, ISourceStore sources, IBuildStore builds, IGrantStore grants, IShareLinkStore links) : PageModel
{
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
        return Page();
    }
}
