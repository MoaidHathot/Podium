namespace Podium.Core.Models;

/// <summary>
/// A GitHub repository (or subtree of one) that Podium scans for decks.
/// </summary>
public sealed record Source
{
    /// <summary>Stable id: "owner/repo" lower-cased.</summary>
    public required string Id { get; init; }
    public required string Owner { get; init; }
    public required string Repo { get; init; }
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
    /// <summary>Commit SHA the deck content was last changed at.</summary>
    public string? LastCommitSha { get; init; }
    public DateTimeOffset? LastCommitAt { get; init; }
    /// <summary>Id of the most recent successful build, if any.</summary>
    public string? CurrentBuildId { get; init; }
    /// <summary>Artifacts available in the current build (denormalised for the library view).</summary>
    public bool CurrentHasPdf { get; init; }
    public bool CurrentHasPptx { get; init; }
    public bool CurrentHasThumbnail { get; init; }
    public string? LatestBuildId { get; init; }
    public BuildStatus? LatestBuildStatus { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Soft-delete marker when the deck disappears from the repo.</summary>
    public bool Archived { get; init; }

    public string EntryPath => string.IsNullOrEmpty(Path) ? Entry : $"{Path}/{Entry}";
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
    /// <summary>Features the deck uses that are not supported remotely (reported by the builder).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? TriggeredBy { get; init; }
    /// <summary>Identity of the builder that produced this build (image digest or script hash); used to rebuild after builder upgrades.</summary>
    public string? BuilderVersion { get; init; }
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
    public DateTimeOffset GrantedAt { get; init; } = DateTimeOffset.UtcNow;
}

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
}

public sealed record ViewEvent
{
    public required string DeckSlug { get; init; }
    public required string Principal { get; init; }
    public required ArtifactKind Artifact { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public bool Presenter { get; init; }
}
