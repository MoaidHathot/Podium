using Microsoft.AspNetCore.Mvc.RazorPages;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Web.Security;

namespace Podium.Web.Pages;

public sealed class IndexModel(IDeckStore decks, ISourceStore sources, IViewHistoryStore views, CallerResolver callers) : PageModel
{
    public IReadOnlyList<DeckRow> Decks { get; private set; } = [];
    public IReadOnlyList<DeckRow> Pinned { get; private set; } = [];
    public IReadOnlyList<DeckRow> RecentlyViewed { get; private set; } = [];
    public IReadOnlyList<DeckRow> RecentlyUpdated { get; private set; } = [];
    public IReadOnlyList<Source> Sources { get; private set; } = [];
    public bool AnyBuilding { get; private set; }
    public bool HasSources { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var caller = callers.Resolve(User);
        Sources = await sources.ListAsync(ct);
        HasSources = Sources.Count > 0;
        var sourceMap = Sources.ToDictionary(s => s.Id, StringComparer.Ordinal);

        var all = await decks.ListAsync(includeArchived: false, ct);
        var rows = all.Select(d => new DeckRow(d, sourceMap.GetValueOrDefault(d.SourceId))).ToList();
        Decks = rows.OrderByDescending(r => r.Deck.LastCommitAt ?? r.Deck.UpdatedAt).ToList();
        Pinned = Decks.Where(r => r.Deck.Pinned).ToList();
        // Only worth a section once the library is large enough that "all decks" is not already the recent list.
        RecentlyUpdated = Decks.Count > 9 ? Decks.Where(r => r.Deck.LastCommitAt is not null).Take(6).ToList() : [];
        AnyBuilding = all.Any(d => d.LatestBuildStatus is BuildStatus.Queued or BuildStatus.Running);

        if (caller.Principal is not null)
        {
            var recent = await views.RecentForPrincipalAsync(caller.Principal, 40, ct);
            var bySlug = rows.ToDictionary(r => r.Deck.Slug, StringComparer.Ordinal);
            RecentlyViewed = recent.Where(v => v.Artifact == ArtifactKind.Site).Select(v => v.DeckSlug).Distinct().Take(6)
                .Select(s => bySlug.GetValueOrDefault(s)).Where(r => r is not null).Cast<DeckRow>().ToList();
        }
    }
}

public sealed record DeckRow(Deck Deck, Source? Source)
{
    public string RepoLabel => Source?.FullName ?? Deck.SourceId;
    public string KindLabel => Deck.Kind switch
    {
        DeckKind.Slidev => "Slidev",
        DeckKind.Presenterm => "presenterm",
        DeckKind.Static => "HTML",
        DeckKind.GitPitch => "GitPitch",
        _ => "Unknown",
    };
    public bool Servable => Deck.CurrentBuildId is not null;
    public bool IsSlidev => Deck.Kind == DeckKind.Slidev;
    public string StatusClass => Deck.LatestBuildStatus switch
    {
        BuildStatus.Succeeded => "dot-ok",
        BuildStatus.Failed => "dot-bad",
        BuildStatus.Queued or BuildStatus.Running => "dot-busy",
        _ => "",
    };
    public string StatusLabel => Deck.Kind == DeckKind.GitPitch ? "Not renderable" : Deck.LatestBuildStatus switch
    {
        BuildStatus.Succeeded => "Built",
        BuildStatus.Failed => Deck.CurrentBuildId is null ? "Build failed" : "Last build failed (serving previous)",
        BuildStatus.Queued => "Queued",
        BuildStatus.Running => "Building",
        _ => "Not built",
    };
    public string SearchText => string.Join(' ', Deck.Title, Deck.Slug, Deck.Path, RepoLabel, KindLabel, Deck.Author ?? "", string.Join(' ', Deck.Tags), Deck.Visibility.ToString()).ToLowerInvariant();
}
