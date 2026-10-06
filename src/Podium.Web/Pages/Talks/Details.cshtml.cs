using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Core.Security;
using Podium.Web.Configuration;
using Podium.Web.Security;

namespace Podium.Web.Pages.Talks;

/// <summary>One talk: abstract and parts, variants, events. Owner sees everything; others only public talks.</summary>
public sealed class DetailsModel(ITalkStore talks, IDeckStore decks, ISourceStore sources, ISessionStore sessions, CallerResolver callers, IOptions<PodiumOptions> options) : PageModel
{
    public Caller Caller { get; private set; } = Caller.Anonymous;
    [BindProperty(SupportsGet = true, Name = "public")] public string? PublicFlag { get; set; }
    public bool Preview => PublicFlag is "1" or "true";
    public bool IsOwner => Caller.IsOwner && !Preview;
    public bool IsPreview => Caller.IsOwner && Preview;
    public Talk Talk { get; private set; } = null!;
    public TalkView View { get; private set; } = null!;
    public Source? Source { get; private set; }
    public Speaker? Speaker { get; private set; }
    public IReadOnlyList<Deck> Decks { get; private set; } = [];
    /// <summary>Live-session recaps linked to submissions, by session id.</summary>
    public IReadOnlyDictionary<string, Session> LinkedSessions { get; private set; } = new Dictionary<string, Session>();
    public string Origin => options.Value.PublicBaseUrl.ToString().TrimEnd('/');

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        Caller = callers.Resolve(User);
        if (!Podium.Core.Slug.IsValid(id)) return NotFound();
        var talk = await talks.GetAsync(id, ct);
        if (talk is null || talk.Archived) return NotFound();
        Source = await sources.GetAsync(talk.SourceId, ct);
        if (!IsOwner && !(talk.Public && talk.Status != "draft" && Source is { Trusted: true })) return NotFound();
        Talk = talk;
        Speaker = await talks.GetSpeakerAsync(talk.SourceId, ct);
        var all = new List<Deck>();
        foreach (var slug in talk.DeckSlugs)
        {
            var d = await decks.GetAsync(slug, ct);
            if (d is { Archived: false } && (IsOwner || d.Visibility == Visibility.Public)) all.Add(d);
        }
        Decks = all;
        View = new TalkView(talk, all, Source, IsOwner);
        if (IsOwner)
        {
            var linked = new Dictionary<string, Session>(StringComparer.Ordinal);
            foreach (var s in talk.Submissions.Where(s => s.SessionId is not null && s.SessionDeckSlug is not null))
            {
                var session = await sessions.GetAsync(s.SessionDeckSlug!, s.SessionId!, ct);
                if (session is not null) linked[s.SessionId!] = session;
            }
            LinkedSessions = linked;
        }
        return Page();
    }

    /// <summary>The bio to show with this talk: the talk's own ## Bio, else the speaker's short bio.</summary>
    public string? Bio => Talk.Part("bio")?.Markdown ?? Speaker?.ShortBio;

    public string EditUrl(string relative) => Source is null ? "" : $"https://github.com/{Source.FullName}/edit/{Uri.EscapeDataString(Source.Ref ?? "HEAD")}/{Talk.Path}{(Talk.Path.Length > 0 ? "/" : "")}{relative}";
    public string NewSubmissionUrl()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var content = $"---\nevent: Event name\ndate: {today:yyyy-MM-dd}\nstatus: submitted\nformat: talk\nduration: {(Talk.Durations.FirstOrDefault() is var d and > 0 ? d : 45)}\n# title: Title as submitted, if different\n# deck: {(Decks.FirstOrDefault()?.Entry ?? "slides.md")}\n# location: City\n# url: https://\n# recording: https://\n---\n";
        return View.NewSubmissionUrl($"{today:yyyy-MM-dd}-event.md", content);
    }
}
