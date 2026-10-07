namespace Podium.Core.Models;

/// <summary>
/// A GitHub repository (or subtree of one) that Podium scans for decks.
/// </summary>
public sealed record Source
{
    /// <summary>Stable key: "owner/repo" lower-cased at registration time. Decks reference it; it survives renames.</summary>
    public required string Id { get; init; }
    /// <summary>Current GitHub owner and name; updated when GitHub reports a rename or transfer.</summary>
    public required string Owner { get; init; }
    public required string Repo { get; init; }
    /// <summary>GitHub's numeric repository id; the identity that survives renames and transfers.</summary>
    public long? RepoId { get; init; }
    /// <summary>Branch or tag to track. Null = repository default branch.</summary>
    public string? Ref { get; init; }
    /// <summary>GitHub App installation id when the App is installed on this repo; null for external/public repos.</summary>
    public long? InstallationId { get; init; }
    /// <summary>True when the owner controls the repo. Untrusted sources default to Private decks and stricter build limits.</summary>
    public bool Trusted { get; init; }
    public bool IsPrivateRepo { get; init; }
    /// <summary>Last commit SHA seen on the tracked ref.</summary>
    public string? LastSeenSha { get; init; }
    public DateTimeOffset? LastScannedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string FullName => $"{Owner}/{Repo}";

    public static string MakeId(string owner, string repo) => $"{owner}/{repo}".ToLowerInvariant();
}

/// <summary>A single presentation inside a source.</summary>
public sealed record Deck
{
    /// <summary>URL-safe, globally unique slug used in /d/{slug}/.</summary>
    public required string Slug { get; init; }
    /// <summary>Optional short alias (also unique across decks); /d/{alias}/ redirects to the canonical slug.</summary>
    public string? Alias { get; init; }
    /// <summary>
    /// Variant label for a deck that is one of several entry files in its folder (Slidev <c>slides.&lt;variant&gt;.md</c>,
    /// presenterm dated files): null for the folder's main deck. Its slug is the folder slug plus the label.
    /// </summary>
    public string? Variant { get; init; }
    /// <summary>The talk this deck belongs to (its folder has an abstract.md, or .podium.yml says <c>talk:</c>).</summary>
    public string? TalkId { get; init; }
    public required string SourceId { get; init; }
    /// <summary>Directory of the deck relative to repo root, forward slashes, no leading/trailing slash. Empty for repo root.</summary>
    public required string Path { get; init; }
    /// <summary>Entry file relative to <see cref="Path"/> (slides.md, main.md, deck.html ...).</summary>
    public required string Entry { get; init; }
    public required DeckKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string? Author { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public Visibility Visibility { get; init; } = Visibility.Private;
    public Visibility PdfVisibility { get; init; } = Visibility.Private;
    public Visibility PptxVisibility { get; init; } = Visibility.Private;
    public bool Pinned { get; init; }
    /// <summary>Whether to produce PDF / PPTX exports on build.</summary>
    public bool ExportPdf { get; init; } = true;
    public bool ExportPptx { get; init; }
    /// <summary>PowerPoint decks only: in-browser viewer to use.</summary>
    public PptxViewer PptxViewer { get; init; } = PptxViewer.Pdf;
    /// <summary>Slidev decks: serve viewers (anyone but the owner) a build variant without speaker notes.</summary>
    public bool StripNotesForViewers { get; init; } = true;
    /// <summary>Trusted repositories only: allow npm lifecycle scripts while installing the deck's dependencies.</summary>
    public bool NpmScripts { get; init; }
    /// <summary>Public decks only: allow other sites to embed the deck in an iframe.</summary>
    public bool AllowEmbedding { get; init; }
    /// <summary>Register a service worker so the deck keeps working when the venue network drops.</summary>
    public bool OfflineCache { get; init; } = true;
    /// <summary>Hold Podium deployments while a live session of this deck is running (opt-in, see Session).</summary>
    public bool HoldDeploysWhileLive { get; init; }
    /// <summary>Audience features offered to the room during live sessions (reactions, questions, polls).</summary>
    public AudienceSettings Audience { get; init; } = AudienceSettings.Default;
    /// <summary>What people who are not presenters may do inside the deck (presenter view, browsing ahead while live).</summary>
    public ViewerSettings Viewers { get; init; } = ViewerSettings.Default;
    /// <summary>Id of the running live session, if any (denormalised for the library badge).</summary>
    public string? LiveSessionId { get; init; }
    /// <summary>Commit SHA the deck content was last changed at.</summary>
    public string? LastCommitSha { get; init; }
    public DateTimeOffset? LastCommitAt { get; init; }
    /// <summary>Id of the build being served (normally the most recent successful one; see <see cref="PinnedBuildId"/>).</summary>
    public string? CurrentBuildId { get; init; }
    /// <summary>When set, the deck is frozen: new successful builds do not replace the served build until the owner promotes one.</summary>
    public string? PinnedBuildId { get; init; }
    /// <summary>Id of the newest successful build (may differ from <see cref="CurrentBuildId"/> while frozen or after a rollback).</summary>
    public string? LatestSuccessfulBuildId { get; init; }
    /// <summary>Artifacts available in the current build (denormalised for the library view).</summary>
    public bool CurrentHasPdf { get; init; }
    public bool CurrentHasPptx { get; init; }
    public bool CurrentHasThumbnail { get; init; }
    public bool CurrentHasPublicSite { get; init; }
    public bool CurrentHasNotes { get; init; }
    public bool CurrentHasText { get; init; }
    public bool CurrentHasSlideSheet { get; init; }
    public int CurrentSlideCount { get; init; }
    /// <summary>
    /// When the author last saved the slides, as far as the served build knows: the document's own metadata for
    /// PowerPoint/PDF decks (null for source decks, whose last commit is the equivalent). The library sorts by
    /// <see cref="SavedAt"/>, so an archive committed years after it was written keeps its real chronology.
    /// </summary>
    public DateTimeOffset? AuthoredAt { get; init; }
    /// <summary>When the document was created, from its own metadata (PowerPoint/PDF).</summary>
    public DateTimeOffset? DocumentCreatedAt { get; init; }
    public string? LatestBuildId { get; init; }
    public BuildStatus? LatestBuildStatus { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Soft-delete marker when the deck disappears from the repo.</summary>
    public bool Archived { get; init; }

    public string EntryPath => string.IsNullOrEmpty(Path) ? Entry : $"{Path}/{Entry}";
    /// <summary>The best date for "when were these slides made": the document's last save, else the last commit that touched the deck.</summary>
    public DateTimeOffset SavedAt => AuthoredAt ?? LastCommitAt ?? UpdatedAt;
}

public sealed record Build
{
    public required string Id { get; init; }
    public required string DeckSlug { get; init; }
    public required string Sha { get; init; }
    public BuildStatus Status { get; init; } = BuildStatus.Queued;
    public DateTimeOffset QueuedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    /// <summary>Opaque id of the execution in the runner (e.g. Container Apps job execution name).</summary>
    public string? RunnerExecutionId { get; init; }
    public string? Error { get; init; }
    public bool HasSite { get; init; }
    public bool HasPdf { get; init; }
    public bool HasPptx { get; init; }
    public bool HasThumbnail { get; init; }
    /// <summary>A second site variant without speaker notes exists (site-public/).</summary>
    public bool HasPublicSite { get; init; }
    public bool HasNotes { get; init; }
    public bool HasText { get; init; }
    public bool HasSlideSheet { get; init; }
    /// <summary>Number of slides (pages) in the deck, when the builder could determine it.</summary>
    public int SlideCount { get; init; }
    /// <summary>When the author last saved the document, read from the file's own metadata (PowerPoint, PDF); null for source decks.</summary>
    public DateTimeOffset? AuthoredAt { get; init; }
    /// <summary>When the document was created, read from the file's own metadata.</summary>
    public DateTimeOffset? DocumentCreatedAt { get; init; }
    /// <summary>Features the deck uses that are not supported remotely (reported by the builder).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Deck-health findings (missing images, oversized assets...) with file positions; mirrored to GitHub check-run annotations.</summary>
    public IReadOnlyList<BuildAnnotation> Annotations { get; init; } = [];
    public string? TriggeredBy { get; init; }
    /// <summary>Identity of the builder that produced this build (image digest or script hash); used to rebuild after builder upgrades.</summary>
    public string? BuilderVersion { get; init; }
    /// <summary>External reference created by a build observer (e.g. GitHub check run id).</summary>
    public string? ExternalRef { get; init; }
}

/// <summary>Explicit access grant for a deck.</summary>
public sealed record Grant
{
    public required string DeckSlug { get; init; }
    /// <summary>Principal id, e.g. "github:1234567".</summary>
    public required string Principal { get; init; }
    public string? DisplayName { get; init; }
    public bool Site { get; init; } = true;
    public bool Pdf { get; init; }
    public bool Pptx { get; init; }
    /// <summary>May open the presenter view with notes and drive the cross-device sync (co-presenter).</summary>
    public bool Present { get; init; }
    public DateTimeOffset GrantedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A deck-health finding reported by the builder. Paths are repository-relative so they map onto check-run annotations.</summary>
public sealed record BuildAnnotation(string Path, int Line, AnnotationLevel Level, string Message);

/// <summary>Signed, revocable share link.</summary>
public sealed record ShareLink
{
    public required string Id { get; init; }
    public required string DeckSlug { get; init; }
    public required ArtifactKind Artifact { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool Revoked { get; init; }
    public string? Label { get; init; }
    /// <summary>PBKDF2 hash of an optional passcode visitors must enter once per browser; null = no passcode.</summary>
    public string? PasscodeHash { get; init; }
    /// <summary>Maximum number of browsers that may open the link (counted when the share cookie is issued); null = unlimited.</summary>
    public int? MaxUses { get; init; }
    /// <summary>How many times the link was opened (share cookie issued).</summary>
    public int Opens { get; init; }
    public DateTimeOffset? LastOpenedAt { get; init; }
    /// <summary>Live session this link was minted for; the link dies with the session.</summary>
    public string? SessionId { get; init; }
}

public sealed record ViewEvent
{
    public required string DeckSlug { get; init; }
    public required string Principal { get; init; }
    public required ArtifactKind Artifact { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public bool Presenter { get; init; }
    /// <summary>Share link the viewer came through, when access was granted by a link.</summary>
    public string? LinkId { get; init; }
}

/// <summary>
/// A live presentation of a deck: explicitly started and ended by the owner. While running, the deck is frozen, a
/// join link exists and the sync hub records navigation so a pacing recap can be written at the end.
/// </summary>
public sealed record Session
{
    public required string Id { get; init; }
    public required string DeckSlug { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>Planned talk length; drives the remote's countdown and the auto-end grace period.</summary>
    public int? PlannedMinutes { get; init; }
    public bool HoldDeploys { get; init; }
    /// <summary>Join link minted for the session (revoked when it ends).</summary>
    public string? LinkId { get; init; }
    /// <summary>Short code the room can type (/j/{code}); resolves to the join link while the session is live.</summary>
    public string? JoinCode { get; init; }
    /// <summary>Whether the deck was frozen by the session start (and should be unfrozen at the end).</summary>
    public bool FrozeDeck { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset? LastPresenterSeenAt { get; init; }
    /// <summary>Why the session ended: manual, idle, overtime, cap.</summary>
    public string? EndReason { get; init; }
    public SessionRecap? Recap { get; init; }
    /// <summary>A rehearsal: recorded like any session but labelled so recaps and pace comparisons can tell them apart.</summary>
    public bool Rehearsal { get; init; }
    /// <summary>Audience features in effect for this session (null: the deck's settings at the time).</summary>
    public AudienceSettings? Audience { get; init; }
    /// <summary>The presenter silenced the room (reactions, questions and votes are dropped while true).</summary>
    public bool AudienceMuted { get; init; }
    /// <summary>Reactions, questions and polls; snapshotted every minute while live, final at the end.</summary>
    public AudienceRecap? AudienceRecap { get; init; }
}

/// <summary>Pacing data computed when a session ends.</summary>
public sealed record SessionRecap(
    int DurationSeconds,
    int PeakViewers,
    int SlidesVisited,
    /// <summary>Seconds spent per slide index (1-based keys), summed over revisits.</summary>
    IReadOnlyDictionary<int, int> SecondsPerSlide,
    int? LastSlide);

/// <summary>Which audience features a deck (or a session) offers the room through the join link.</summary>
public sealed record AudienceSettings(
    bool Reactions = true,
    bool Questions = true,
    bool Polls = true,
    /// <summary>Reactions float across the projector (and viewers' screens), not just the presenter's counters.</summary>
    bool FloatReactions = true,
    /// <summary>Questions may carry a nickname; otherwise they are anonymous.</summary>
    bool Nicknames = true)
{
    public static readonly AudienceSettings Default = new();
    public bool Any => Reactions || Questions || Polls;
}

/// <summary>
/// Courtesy locks for non-presenters. They keep an audience from wandering (the slides are part of the deck bundle,
/// so they are not a secret-keeping mechanism; speaker notes are kept away by the notes-free build instead).
/// </summary>
public sealed record ViewerSettings(
    /// <summary>Viewers may open the presenter view and the notes viewer (Slidev decks).</summary>
    bool PresenterView = false,
    /// <summary>While a session is live, viewers may move past the presenter's slide (and open the overview).</summary>
    bool BrowseAhead = false)
{
    public static readonly ViewerSettings Default = new();
}

/// <summary>A question asked by the room during a session.</summary>
public sealed record AudienceQuestion(
    string Id,
    string Text,
    string? Nick,
    DateTimeOffset At,
    int Slide,
    int Upvotes,
    bool Answered,
    bool Dismissed,
    bool Pinned);

public sealed record PollOption(string Text, int Votes);

/// <summary>A poll the presenter ran; Open accepts votes, Shown renders results on the projector.</summary>
public sealed record AudiencePoll(
    string Id,
    string Question,
    IReadOnlyList<PollOption> Options,
    DateTimeOffset CreatedAt,
    int Slide,
    bool Open,
    bool Shown,
    int TotalVotes,
    /// <summary>Client ids that voted (random per browser; lets a restart keep one vote per client).</summary>
    IReadOnlyList<string> Voters,
    /// <summary>Voters may replace their answer while the poll is open (otherwise the first answer is final).</summary>
    bool AllowChange = false);

/// <summary>Everything the room contributed during a session.</summary>
public sealed record AudienceRecap(
    IReadOnlyDictionary<string, int> Reactions,
    IReadOnlyList<AudienceQuestion> Questions,
    IReadOnlyList<AudiencePoll> Polls)
{
    public static readonly AudienceRecap Empty = new(new Dictionary<string, int>(), [], []);
    public int ReactionTotal => Reactions.Values.Sum();
}

/// <summary>A signed-in visitor asking for access to a Shared/Private deck.</summary>
public sealed record AccessRequest
{
    public required string DeckSlug { get; init; }
    /// <summary>Principal id, e.g. "github:1234567".</summary>
    public required string Principal { get; init; }
    public string? DisplayName { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
    public AccessRequestStatus Status { get; init; } = AccessRequestStatus.Pending;
    public DateTimeOffset? DecidedAt { get; init; }
}

/// <summary>Who changed what, when. Written by every mutating owner action.</summary>
public sealed record AuditEntry
{
    public required string Id { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Actor principal ("github:123") or "system" for automatic actions.</summary>
    public required string Actor { get; init; }
    /// <summary>Verb-object, e.g. "deck.visibility", "link.create", "grant.delete", "session.start".</summary>
    public required string Action { get; init; }
    /// <summary>Deck slug or other subject the action applied to.</summary>
    public string? Target { get; init; }
    public string? Details { get; init; }
    public string? Ip { get; init; }
}

/// <summary>
/// One signed-in browser (a cookie issued at login). Lets the owner see where they are signed in and sign out a
/// single device; "sign out everywhere" still covers tickets that predate device tracking.
/// </summary>
public sealed record Device
{
    /// <summary>Principal id, e.g. "github:1234567".</summary>
    public required string Principal { get; init; }
    /// <summary>Random id carried by the cookie (urn:podium:sid).</summary>
    public required string Sid { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Short, human description of the browser/OS (never the raw user agent).</summary>
    public string? Client { get; init; }
    public string? Ip { get; init; }
    public bool Revoked { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

/// <summary>A named section of a talk, submission or speaker file: the text under a <c>## Heading</c>.</summary>
public sealed record TalkPart(string Name, string Markdown)
{
    /// <summary>Heading normalised for lookups: lower case, letters/digits only ("Short abstract" -> "shortabstract").</summary>
    public string Key => Normalize(Name);
    public static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>
/// A session you can give: the canonical abstract and its parts, how long it runs, which decks are its variants and
/// where it was submitted or delivered. Read from <c>abstract.md</c> in a deck folder (trusted sources only) and the
/// files under <c>submissions/</c> next to it; decks in other folders join with <c>talk:</c> in their .podium.yml.
/// </summary>
public sealed record Talk
{
    /// <summary>"{repo}-{folder}" by default, or "{repo}-{id}" when abstract.md sets <c>id:</c>; unique across sources.</summary>
    public required string Id { get; init; }
    /// <summary>The id as written in the repository (what <c>talk:</c> references), without the repo prefix.</summary>
    public required string LocalId { get; init; }
    public required string SourceId { get; init; }
    /// <summary>Folder holding abstract.md (the talk's home).</summary>
    public required string Path { get; init; }
    public string Title { get; init; } = "";
    /// <summary>beginner | intermediate | advanced (free text, lower case).</summary>
    public string? Level { get; init; }
    /// <summary>Formats you can do, in minutes.</summary>
    public IReadOnlyList<int> Durations { get; init; } = [];
    /// <summary>available | draft | retired.</summary>
    public string Status { get; init; } = "available";
    /// <summary>Listed on the public speaker page.</summary>
    public bool Public { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>The canonical abstract: the body of abstract.md before the first <c>##</c> heading.</summary>
    public string Abstract { get; init; } = "";
    /// <summary>Further sections (Short abstract, Outline, Takeaways, Bio, ...).</summary>
    public IReadOnlyList<TalkPart> Parts { get; init; } = [];
    public IReadOnlyList<Submission> Submissions { get; init; } = [];
    /// <summary>Slugs of the decks that are variants of this talk (folder members first, then joined decks).</summary>
    public IReadOnlyList<string> DeckSlugs { get; init; } = [];
    public DateTimeOffset? LastCommitAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Archived { get; init; }

    public TalkPart? Part(string name) { var k = TalkPart.Normalize(name); return Parts.FirstOrDefault(p => p.Key == k); }
    /// <summary>The short abstract when there is one, else the first paragraph of the canonical abstract.</summary>
    public string ShortAbstract => Part("short abstract")?.Markdown ?? Part("short")?.Markdown ?? FirstParagraph(Abstract);
    public static string FirstParagraph(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n").Trim();
        var i = text.IndexOf("\n\n", StringComparison.Ordinal);
        return i < 0 ? text : text[..i].Trim();
    }
}

/// <summary>One event a talk was submitted to or delivered at: a file under <c>submissions/</c>.</summary>
public sealed record Submission
{
    /// <summary>File name without extension; stable across syncs, so Podium-side links (the live session) survive edits.</summary>
    public required string Key { get; init; }
    public string Event { get; init; } = "";
    public DateOnly? Date { get; init; }
    /// <summary>How precise <see cref="Date"/> is: day (default), month (<c>date: 2017-09</c>) or year (<c>date: 2019</c>) for historical records.</summary>
    public string DatePrecision { get; init; } = "day";
    /// <summary>submitted | accepted | declined | delivered | cancelled.</summary>
    public string Status { get; init; } = "submitted";
    /// <summary>talk | workshop | lightning | keynote | panel | course | webinar.</summary>
    public string Format { get; init; } = "talk";
    public int? Duration { get; init; }
    /// <summary>Title as submitted, when it differs from the talk's.</summary>
    public string? Title { get; init; }
    /// <summary>The deck used: an entry file name in the talk folder, a repository path, or a deck slug.</summary>
    public string? Deck { get; init; }
    /// <summary>Resolved slug of <see cref="Deck"/> (null when it did not match a known deck).</summary>
    public string? DeckSlug { get; init; }
    public string? Location { get; init; }
    public string? Url { get; init; }
    public string? Recording { get; init; }
    public string? Notes { get; init; }
    /// <summary>Abstract exactly as submitted (empty: the talk's canonical abstract applied).</summary>
    public string? Abstract { get; init; }
    public IReadOnlyList<TalkPart> Parts { get; init; } = [];
    /// <summary>Live session Podium matched to this delivery (set when a session on the deck ends around the date).</summary>
    public string? SessionId { get; init; }
    public string? SessionDeckSlug { get; init; }

    public string EffectiveTitle(Talk talk) => string.IsNullOrWhiteSpace(Title) ? talk.Title : Title!;
    public string EffectiveAbstract(Talk talk) => string.IsNullOrWhiteSpace(Abstract) ? talk.Abstract : Abstract!;
    public bool IsPublicFact => Status is "accepted" or "delivered";
    public TalkPart? Part(string name) { var k = TalkPart.Normalize(name); return Parts.FirstOrDefault(p => p.Key == k); }
    /// <summary>The date as precisely as it is known: "May 20, 2025", "Sep 2017" or "2019"; the compact form drops the day.</summary>
    public string? DateLabel(bool compact = false) => Date is not { } d ? null : DatePrecision switch
    {
        "year" => d.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
        "month" => d.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
        _ => d.ToString(compact ? "MMM yyyy" : "MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>The speaker, from <c>speaker.md</c> at the repository root: bio variants, photo, links.</summary>
public sealed record Speaker
{
    public required string SourceId { get; init; }
    public string Name { get; init; } = "";
    public string? Tagline { get; init; }
    public string? Photo { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Links { get; init; } = [];
    /// <summary>Body before the first <c>##</c>: the default (long) bio.</summary>
    public string Bio { get; init; } = "";
    public IReadOnlyList<TalkPart> Parts { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public TalkPart? Part(string name) { var k = TalkPart.Normalize(name); return Parts.FirstOrDefault(p => p.Key == k); }
    public string ShortBio => Part("short bio")?.Markdown ?? Part("short")?.Markdown ?? Talk.FirstParagraph(Bio);
}
