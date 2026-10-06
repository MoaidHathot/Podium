using Microsoft.AspNetCore.Mvc.RazorPages;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Web.Security;

namespace Podium.Web.Pages;

public sealed class IndexModel(IDeckStore decks, ISourceStore sources, IViewHistoryStore views, CallerResolver callers, IAccessRequestStore accessRequests, ISessionStore sessions, ITalkStore talks) : PageModel
{
    public IReadOnlyList<AccessRequest> PendingRequests { get; private set; } = [];
    public IReadOnlyList<DeckRow> Decks { get; private set; } = [];
    public IReadOnlyList<DeckRow> Pinned { get; private set; } = [];
    public IReadOnlyList<DeckRow> RecentlyViewed { get; private set; } = [];
    public IReadOnlyList<Source> Sources { get; private set; } = [];
    public IReadOnlyList<DeckRow> Archived { get; private set; } = [];
    /// <summary>Sessions running right now, newest first, with their decks (for the "Live now" banner).</summary>
    public IReadOnlyList<(Session Session, Deck Deck)> LiveNow { get; private set; } = [];
    public bool AnyBuilding { get; private set; }
    public bool HasSources { get; private set; }
    public bool HasTalks { get; private set; }
    /// <summary>Set when /remote found nothing to open (no live session, nothing presented yet).</summary>
    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)] public string? Remote { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var caller = callers.Resolve(User);
        Sources = await sources.ListAsync(ct);
        HasSources = Sources.Count > 0;
        var sourceMap = Sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var talkMap = (await talks.ListAsync(includeArchived: false, ct)).ToDictionary(t => t.Id, StringComparer.Ordinal);
        HasTalks = talkMap.Count > 0;

        var lastViewed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, DateTimeOffset> dismissed = new Dictionary<string, DateTimeOffset>();
        if (caller.Principal is not null)
        {
            foreach (var v in await views.RecentForPrincipalAsync(caller.Principal, 200, ct))
            {
                if (v.Artifact != ArtifactKind.Site) continue;
                if (!lastViewed.TryGetValue(v.DeckSlug, out var at) || v.At > at) lastViewed[v.DeckSlug] = v.At;
            }
            dismissed = await views.GetRecentDismissalsAsync(caller.Principal, ct);
        }

        PendingRequests = await accessRequests.ListPendingAsync(ct);
        var everything = await decks.ListAsync(includeArchived: true, ct);
        var all = everything.Where(d => !d.Archived).ToList();
        var bySlug = everything.ToDictionary(d => d.Slug, StringComparer.Ordinal);
        LiveNow = (await sessions.ListLiveAsync(ct)).OrderByDescending(s => s.StartedAt)
            .Where(s => bySlug.ContainsKey(s.DeckSlug)).Select(s => (s, bySlug[s.DeckSlug])).ToList();
        Talk? TalkOf(Deck d) => d.TalkId is not null ? talkMap.GetValueOrDefault(d.TalkId) : null;
        Archived = everything.Where(d => d.Archived).OrderByDescending(d => d.UpdatedAt).Select(d => new DeckRow(d, sourceMap.GetValueOrDefault(d.SourceId), null) { Talk = TalkOf(d) }).ToList();
        var rows = all.Select(d => new DeckRow(d, sourceMap.GetValueOrDefault(d.SourceId), lastViewed.GetValueOrDefault(d.Slug) is { Ticks: > 0 } lv ? lv : null) { Talk = TalkOf(d) }).ToList();
        Decks = rows.OrderByDescending(r => r.Deck.LastCommitAt ?? r.Deck.UpdatedAt).ToList();
        Pinned = Decks.Where(r => r.Deck.Pinned).ToList();
        // A dismissed deck stays hidden from the shelf until it is presented again (a view newer than the dismissal).
        RecentlyViewed = rows
            .Where(r => r.LastViewed is { } seen && (!dismissed.TryGetValue(r.Deck.Slug, out var gone) || seen > gone))
            .OrderByDescending(r => r.LastViewed).Take(6)
            .Select(r => r with { InRecentShelf = true })
            .ToList();
        AnyBuilding = all.Any(d => d.LatestBuildStatus is BuildStatus.Queued or BuildStatus.Running);
    }
}

public sealed record DeckRow(Deck Deck, Source? Source, DateTimeOffset? LastViewed)
{
    /// <summary>True when rendered inside the "Recently presented" shelf (enables the dismiss action).</summary>
    public bool InRecentShelf { get; init; }
    /// <summary>The talk this deck is a variant of, when its folder has an abstract.md (or .podium.yml joins one).</summary>
    public Talk? Talk { get; init; }
    public string RepoLabel => Source?.FullName ?? Deck.SourceId;
    /// <summary>Label of the variant inside its talk: the explicit label, else "main" when siblings exist.</summary>
    public string? VariantLabel => Deck.Variant ?? (Talk is { DeckSlugs.Count: > 1 } ? "main" : null);
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
    /// <summary>Compact status for the list layout; the full label stays in the tooltip.</summary>
    public string StatusShort => Deck.Kind == DeckKind.GitPitch ? "N/A" : Deck.LatestBuildStatus switch
    {
        BuildStatus.Succeeded => "Built",
        BuildStatus.Failed => "Failed",
        BuildStatus.Queued => "Queued",
        BuildStatus.Running => "Building",
        _ => "Not built",
    };
    public string SearchText => string.Join(' ', Deck.Title, Deck.Slug, Deck.Path, RepoLabel, KindLabel, Deck.Author ?? "", string.Join(' ', Deck.Tags), Deck.Visibility.ToString(), Talk?.Title ?? "", Talk?.LocalId ?? "", Deck.Variant ?? "").ToLowerInvariant();
}
