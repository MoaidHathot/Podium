using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Core.Security;
using Podium.Web.Configuration;
using Podium.Web.Security;

namespace Podium.Web.Pages;

/// <summary>
/// Portfolio of Public decks for everyone who is not the owner. Enabled by Podium:PublicGallery; otherwise 404 so a
/// deployment that never opted in stays login-only. Only decks whose site is Public appear (artifact-only public
/// exports are listed with their export links); nothing here needs a session.
/// </summary>
public sealed class GalleryModel(IDeckStore decks, ITalkStore talks, ISourceStore sources, CallerResolver callers, IOptions<PodiumOptions> options) : PageModel
{
    public IReadOnlyList<Deck> Decks { get; private set; } = [];
    public IReadOnlyList<string> Tags { get; private set; } = [];
    public Caller Caller { get; private set; } = Caller.Anonymous;
    public string Title => options.Value.GalleryTitle ?? "Decks";
    public string Origin => options.Value.PublicBaseUrl.ToString().TrimEnd('/');
    [BindProperty(SupportsGet = true)] public string? Tag { get; set; }
    /// <summary>Public talks (public: true, not draft, trusted source) by id; drives descriptions and "about this talk" links.</summary>
    private Dictionary<string, Talk> _publicTalks = new(StringComparer.Ordinal);
    public bool HasPublicTalks => _publicTalks.Count > 0;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!options.Value.PublicGallery) return NotFound();
        Caller = callers.Resolve(User);
        if (Caller.IsOwner) return Redirect("/");
        var all = await decks.ListAsync(includeArchived: false, ct);
        var visible = all.Where(d => d.CurrentBuildId is not null && (d.Visibility == Visibility.Public || d.PdfVisibility == Visibility.Public || d.PptxVisibility == Visibility.Public))
            .OrderByDescending(d => d.SavedAt)
            .ToList();
        Tags = visible.SelectMany(d => d.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).Take(30).ToList();
        if (!string.IsNullOrWhiteSpace(Tag))
        {
            Tag = Tag.Trim().ToLowerInvariant();
            if (Tag.Length > 60) Tag = Tag[..60];
            visible = visible.Where(d => d.Tags.Contains(Tag, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        Decks = visible;
        var trusted = (await sources.ListAsync(ct)).Where(s => s.Trusted).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        _publicTalks = (await talks.ListAsync(includeArchived: false, ct)).Where(t => t.Public && t.Status != "draft" && trusted.Contains(t.SourceId)).ToDictionary(t => t.Id, StringComparer.Ordinal);
        return Page();
    }

    public Talk? PublicTalkOf(Deck deck) => deck.TalkId is not null ? _publicTalks.GetValueOrDefault(deck.TalkId) : null;

    /// <summary>The deck's own description, else the short abstract of its (public) talk.</summary>
    public string? DescriptionOf(Deck deck)
    {
        if (!string.IsNullOrWhiteSpace(deck.Description)) return deck.Description;
        return PublicTalkOf(deck) is { } talk ? Markdown.ToText(talk.ShortAbstract) : null;
    }
}
