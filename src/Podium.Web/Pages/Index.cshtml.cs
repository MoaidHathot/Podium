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
    public IReadOnlyList<Source> Sources { get; private set; } = [];
    public bool AnyBuilding { get; private set; }
    public bool HasSources { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var caller = callers.Resolve(User);
        Sources = await sources.ListAsync(ct);
        HasSources = Sources.Count > 0;
        var sourceMap = Sources.ToDictionary(s => s.Id, StringComparer.Ordinal);

        var lastViewed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (caller.Principal is not null)
        {
            foreach (var v in await views.RecentForPrincipalAsync(caller.Principal, 200, ct))
            {
                if (v.Artifact != ArtifactKind.Site) continue;
                if (!lastViewed.TryGetValue(v.DeckSlug, out var at) || v.At > at) lastViewed[v.DeckSlug] = v.At;
            }
        }

        var all = await decks.ListAsync(includeArchived: false, ct);
        var rows = all.Select(d => new DeckRow(d, sourceMap.GetValueOrDefault(d.SourceId), lastViewed.GetValueOrDefault(d.Slug) is { Ticks: > 0 } lv ? lv : null)).ToList();
        Decks = rows.OrderByDescending(r => r.Deck.LastCommitAt ?? r.Deck.UpdatedAt).ToList();
        Pinned = Decks.Where(r => r.Deck.Pinned).ToList();
        RecentlyViewed = rows.Where(r => r.LastViewed is not null).OrderByDescending(r => r.LastViewed).Take(6).ToList();
        AnyBuilding = all.Any(d => d.LatestBuildStatus is BuildStatus.Queued or BuildStatus.Running);
    }
}

public sealed record DeckRow(Deck Deck, Source? Source, DateTimeOffset? LastViewed)
{
    public string RepoLabel => Source?.FullName ?? Deck.SourceId;
    public string KindLabel => Deck.Kind switch
    {
        DeckKind.Slidev => "Slidev",
        DeckKind.Presenterm => "presenterm",
        DeckKind.Static => "HTML",
        DeckKind.GitPitch => "GitPitch",
        DeckKind.PowerPoint => "PowerPoint",
        DeckKind.Pdf => "PDF",
        _ => "Unknown",
    };
    public string KindCss => Deck.Kind.ToString().ToLowerInvariant();
    public bool HasThumbnail => Deck.CurrentHasThumbnail && Deck.CurrentBuildId is not null;
    public string ThumbnailUrl => $"/d/{Deck.Slug}.jpg?v={Deck.CurrentBuildId}";
    public bool Servable => Deck.CurrentBuildId is not null;
    public bool IsSlidev => Deck.Kind == DeckKind.Slidev;
    public DateTimeOffset Updated => Deck.LastCommitAt ?? Deck.UpdatedAt;
    public string VisibilityLabel => Deck.Visibility switch
    {
        Visibility.Public => "Public",
        Visibility.Link => "Link",
        Visibility.Shared => "Shared",
        _ => "Private",
    };
    public string StatusKey => Deck.LatestBuildStatus switch
    {
        BuildStatus.Queued or BuildStatus.Running => "building",
        BuildStatus.Failed => "failed",
        _ => "ok",
    };
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
