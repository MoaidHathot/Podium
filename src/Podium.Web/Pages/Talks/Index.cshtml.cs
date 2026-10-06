using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Core.Security;
using Podium.Web.Configuration;
using Podium.Web.Security;

namespace Podium.Web.Pages.Talks;

/// <summary>
/// The talk catalog. For the owner: every talk with its variants, submissions and status, searchable. For everyone
/// else: the speaker page, listing only talks whose abstract.md says <c>public: true</c> (and is not a draft) with
/// accepted/delivered events, the speaker bio and links to Public decks; 404 when nothing is public.
/// </summary>
public sealed class IndexModel(ITalkStore talks, IDeckStore decks, ISourceStore sources, CallerResolver callers, IOptions<PodiumOptions> options) : PageModel
{
    public Caller Caller { get; private set; } = Caller.Anonymous;
    /// <summary>Owner looking at the page as organisers see it (?public=1).</summary>
    [BindProperty(SupportsGet = true, Name = "public")] public string? PublicFlag { get; set; }
    public bool Preview => PublicFlag is "1" or "true";
    public bool IsOwner => Caller.IsOwner && !Preview;
    public bool IsPreview => Caller.IsOwner && Preview;
    public IReadOnlyList<TalkView> Talks { get; private set; } = [];
    public IReadOnlyList<Speaker> Speakers { get; private set; } = [];
    public IReadOnlyList<Source> Sources { get; private set; } = [];
    public IReadOnlyList<string> Tags { get; private set; } = [];
    public string Origin => options.Value.PublicBaseUrl.ToString().TrimEnd('/');
    public bool GalleryEnabled => options.Value.PublicGallery;
    [BindProperty(SupportsGet = true)] public string? Tag { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }

    /// <summary>
    /// The speaker photo as a URL the browser may load: an absolute https URL as written, or a repository-relative
    /// path resolved against the source's raw.githubusercontent.com tree. Anything else (javascript:, data:, ..)
    /// renders no photo.
    /// </summary>
    public string? PhotoUrl(Speaker speaker) => TalkLinks.PhotoUrl(speaker.Photo, Sources.FirstOrDefault(s => s.Id == speaker.SourceId));

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Caller = callers.Resolve(User);
        Sources = await sources.ListAsync(ct);
        var all = await talks.ListAsync(includeArchived: false, ct);
        var allDecks = (await decks.ListAsync(includeArchived: false, ct)).ToDictionary(d => d.Slug, StringComparer.Ordinal);
        Speakers = (await talks.ListSpeakersAsync(ct)).Where(s => Sources.Any(src => src.Id == s.SourceId && src.Trusted)).ToList();

        var visible = IsOwner ? all : all.Where(t => t.Public && t.Status != "draft" && Sources.Any(s => s.Id == t.SourceId && s.Trusted)).ToList();
        if (!IsOwner && visible.Count == 0 && !IsPreview) return NotFound();

        Tags = visible.SelectMany(t => t.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).Take(30).ToList();
        if (!string.IsNullOrWhiteSpace(Tag))
        {
            Tag = Tag.Trim().ToLowerInvariant();
            if (Tag.Length > 60) Tag = Tag[..60];
            visible = visible.Where(t => t.Tags.Contains(Tag, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        if (IsOwner && !string.IsNullOrWhiteSpace(Status) && Podium.Core.Discovery.TalkFiles.Statuses.Contains(Status))
            visible = visible.Where(t => t.Status == Status).ToList();

        Talks = visible
            .Select(t => new TalkView(t, t.DeckSlugs.Select(s => allDecks.GetValueOrDefault(s)).Where(d => d is not null && (IsOwner || d.Visibility == Visibility.Public)).Select(d => d!).ToList(), Sources.FirstOrDefault(s => s.Id == t.SourceId), IsOwner))
            .OrderBy(v => v.Talk.Status == "available" ? 0 : v.Talk.Status == "draft" ? 1 : 2)
            .ThenByDescending(v => v.LastActivity)
            .ThenBy(v => v.Talk.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Page();
    }
}

/// <summary>Links Podium derives from a talk's place in its repository (GitHub edit/new-file URLs, raw photo URLs).</summary>
public static class TalkLinks
{
    public static string? PhotoUrl(string? photo, Source? source)
    {
        if (string.IsNullOrWhiteSpace(photo)) return null;
        if (Markdown.SafeLink(photo) is { } absolute) return absolute.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? null : absolute;
        if (source is null || photo.Contains("..", StringComparison.Ordinal) || photo.Contains(':', StringComparison.Ordinal) || photo.Contains('\\', StringComparison.Ordinal)) return null;
        var path = photo.Trim().TrimStart('/');
        if (path.Length == 0 || path.Length > 300) return null;
        return $"https://raw.githubusercontent.com/{Uri.EscapeDataString(source.Owner)}/{Uri.EscapeDataString(source.Repo)}/{Uri.EscapeDataString(source.Ref ?? "HEAD")}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
    }
}

/// <summary>A talk with the decks and events the current viewer may see.</summary>
public sealed record TalkView(Talk Talk, IReadOnlyList<Deck> Decks, Source? Source, bool Owner)
{
    /// <summary>Events the viewer may see, newest first (undated last).</summary>
    public IReadOnlyList<Submission> Submissions => (Owner ? Talk.Submissions : Talk.Submissions.Where(s => s.IsPublicFact)).OrderByDescending(s => s.Date ?? DateOnly.MinValue).ThenBy(s => s.Event, StringComparer.OrdinalIgnoreCase).ToList();
    public Submission? LastDelivered => Submissions.Where(s => s.Status == "delivered" && s.Date is not null).OrderByDescending(s => s.Date).FirstOrDefault();
    public Submission? NextUp => Submissions.Where(s => s.Status is "accepted" or "submitted" && s.Date is { } d && d >= DateOnly.FromDateTime(DateTime.UtcNow)).OrderBy(s => s.Date).FirstOrDefault();
    public DateTimeOffset LastActivity
    {
        get
        {
            var sub = Submissions.Where(s => s.Date is not null).Select(s => s.Date!.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).DefaultIfEmpty(DateTime.MinValue).Max();
            var commit = Talk.LastCommitAt?.UtcDateTime ?? DateTime.MinValue;
            return new DateTimeOffset(sub > commit ? sub : commit, TimeSpan.Zero);
        }
    }
    public Deck? MainDeck => Decks.FirstOrDefault(d => d.Path == Talk.Path && d.Variant is null) ?? Decks.FirstOrDefault();
    public Deck? Thumbnail => Decks.FirstOrDefault(d => d.CurrentHasThumbnail && d.CurrentBuildId is not null);
    public string Years
    {
        get
        {
            var ys = Submissions.Where(s => s.Date is not null && s.Status is "delivered" or "accepted").Select(s => s.Date!.Value.Year).Distinct().OrderBy(y => y).ToList();
            return ys.Count == 0 ? "" : ys.Count == 1 ? ys[0].ToString() : $"{ys[0]}–{ys[^1]}";
        }
    }
    public string SearchText => string.Join(' ', Talk.Title, Talk.LocalId, Talk.Level ?? "", string.Join(' ', Talk.Tags), Talk.Abstract, string.Join(' ', Talk.Parts.Select(p => p.Markdown)), string.Join(' ', Submissions.Select(s => s.Event + " " + (s.Title ?? "") + " " + (s.Location ?? "")))).ToLowerInvariant();
    public string EditUrl => Source is null ? "" : $"https://github.com/{Source.FullName}/edit/{Uri.EscapeDataString(Source.Ref ?? "HEAD")}/{Talk.Path}{(Talk.Path.Length > 0 ? "/" : "")}abstract.md";
    public string NewSubmissionUrl(string fileName, string content)
        => Source is null ? "" : $"https://github.com/{Source.FullName}/new/{Uri.EscapeDataString(Source.Ref ?? "HEAD")}/{Talk.Path}{(Talk.Path.Length > 0 ? "/" : "")}submissions?filename={Uri.EscapeDataString(fileName)}&value={Uri.EscapeDataString(content)}";
}
