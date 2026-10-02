using Podium.Core.Models;

namespace Podium.Core.Abstractions;

public interface ISourceStore
{
    Task<Source?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Source>> ListAsync(CancellationToken ct = default);
    Task UpsertAsync(Source source, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

public interface IDeckStore
{
    Task<Deck?> GetAsync(string slug, CancellationToken ct = default);
    /// <summary>Resolves an alias to its deck, or null.</summary>
    Task<Deck?> GetByAliasAsync(string alias, CancellationToken ct = default);
    Task<IReadOnlyList<Deck>> ListAsync(bool includeArchived = false, CancellationToken ct = default);
    Task<IReadOnlyList<Deck>> ListBySourceAsync(string sourceId, CancellationToken ct = default);
    Task UpsertAsync(Deck deck, CancellationToken ct = default);
    Task DeleteAsync(string slug, CancellationToken ct = default);
}

public interface IBuildStore
{
    Task<Build?> GetAsync(string deckSlug, string buildId, CancellationToken ct = default);
    Task<IReadOnlyList<Build>> ListForDeckAsync(string deckSlug, int take = 20, CancellationToken ct = default);
    Task<IReadOnlyList<Build>> ListActiveAsync(CancellationToken ct = default);
    Task UpsertAsync(Build build, CancellationToken ct = default);
    Task DeleteAsync(string deckSlug, string buildId, CancellationToken ct = default);
}

public interface IGrantStore
{
    Task<IReadOnlyList<Grant>> ListForDeckAsync(string deckSlug, CancellationToken ct = default);
    Task UpsertAsync(Grant grant, CancellationToken ct = default);
    Task DeleteAsync(string deckSlug, string principal, CancellationToken ct = default);
}

public interface IShareLinkStore
{
    Task<ShareLink?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ShareLink>> ListForDeckAsync(string deckSlug, CancellationToken ct = default);
    Task UpsertAsync(ShareLink link, CancellationToken ct = default);
}

public interface IViewHistoryStore
{
    Task RecordAsync(ViewEvent e, CancellationToken ct = default);
    Task<IReadOnlyList<ViewEvent>> RecentForPrincipalAsync(string principal, int take = 20, CancellationToken ct = default);

    /// <summary>Hides a deck from the principal's "recently presented" list until it is presented again.</summary>
    Task DismissRecentAsync(string principal, string deckSlug, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Deck slug to dismissal time, for the given principal.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> GetRecentDismissalsAsync(string principal, CancellationToken ct = default);

    /// <summary>Recent views of one deck across all principals (newest first), for the owner's analytics panel.</summary>
    Task<IReadOnlyList<ViewEvent>> RecentForDeckAsync(string deckSlug, int take = 100, CancellationToken ct = default);
}

/// <summary>Represents a stored artifact object.</summary>
public sealed record ArtifactObject(Stream Content, string ContentType, long? Length, string? ETag, DateTimeOffset? LastModified) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IArtifactStore
{
    /// <summary>Opens a file from a build's site artifact, e.g. "index.html" or "assets/x.js". Returns null when not found.</summary>
    /// <param name="variant">"site" (full) or "site-public" (speaker notes stripped).</param>
    Task<ArtifactObject?> OpenSiteFileAsync(string deckSlug, string buildId, string relativePath, CancellationToken ct = default, string variant = "site");
    Task<ArtifactObject?> OpenArtifactAsync(string deckSlug, string buildId, ArtifactKind kind, CancellationToken ct = default);
    /// <summary>Creates a scoped, time-limited write URL the builder uses to upload results for one build.</summary>
    Task<Uri> CreateUploadUriAsync(string deckSlug, string buildId, TimeSpan lifetime, CancellationToken ct = default);
    Task DeleteBuildAsync(string deckSlug, string buildId, CancellationToken ct = default);
}

/// <summary>Everything the builder needs; contains short-lived secrets and must never be persisted.</summary>
/// <param name="CloneUrl">HTTPS clone URL including a short-lived token when needed.</param>
/// <param name="CallbackUri">Where the builder posts its completion report.</param>
/// <param name="MaxOutputMegabytes">Upper bound for everything the build may upload; 0 lets the builder apply its default.</param>
public sealed record BuildRequest(
    Build Build,
    Deck Deck,
    Source Source,
    Uri CloneUrl,
    Uri UploadUri,
    Uri CallbackUri,
    string CallbackToken,
    TimeSpan Timeout,
    int MaxOutputMegabytes = 0);

/// <summary>Notified about build lifecycle events (e.g. to mirror them as GitHub check runs). Failures are logged and ignored.</summary>
public interface IBuildObserver
{
    /// <summary>Called after the build has been handed to the runner. May return a provider reference stored on the build.</summary>
    Task<string?> OnStartedAsync(Build build, Deck deck, Source source, CancellationToken ct = default);
    Task OnFinishedAsync(Build build, Deck deck, Source source, CancellationToken ct = default);
}

public interface IBuildRunner
{
    /// <summary>Starts the build somewhere isolated and returns a runner execution id.</summary>
    Task<string> StartAsync(BuildRequest request, CancellationToken ct = default);

    /// <summary>Stable identity of the current builder (image reference/digest or script hash). Changes trigger rebuilds.</summary>
    Task<string> GetBuilderVersionAsync(CancellationToken ct = default);
}

public interface IRepositoryClient
{
    Task<(string Sha, DateTimeOffset CommittedAt)> GetHeadAsync(Source source, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListTreeAsync(Source source, string sha, CancellationToken ct = default);
    Task<string?> ReadTextFileAsync(Source source, string sha, string path, CancellationToken ct = default);
    /// <summary>Returns files changed between two commits (paths relative to repo root).</summary>
    Task<IReadOnlyList<string>> DiffPathsAsync(Source source, string fromSha, string toSha, CancellationToken ct = default);
    Task<(DateTimeOffset CommittedAt, string Sha)?> LastCommitForPathAsync(Source source, string sha, string path, CancellationToken ct = default);
    Task<Uri> GetAuthenticatedCloneUrlAsync(Source source, CancellationToken ct = default);
    Task<(bool IsPrivate, string DefaultBranch, bool CallerIsOwner)> GetRepoInfoAsync(string owner, string repo, CancellationToken ct = default);
}
