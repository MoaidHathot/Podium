using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.InMemory;
using Podium.Core.Models;
using Podium.Core.Services;

namespace Podium.Tests;

/// <summary>Fake repository with a mutable tree; tracks call counts.</summary>
internal sealed class FakeRepo : IRepositoryClient
{
    public string Sha { get; set; } = "aaaaaaa0000000000000000000000000000000001";
    public List<string> Tree { get; } = [];
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    public List<string> Changed { get; } = [];
    public int DiffCalls;
    public int LastCommitCalls;

    public Task<(string Sha, DateTimeOffset CommittedAt)> GetHeadAsync(Source source, CancellationToken ct = default) => Task.FromResult((Sha, DateTimeOffset.UtcNow));
    public Task<IReadOnlyList<string>> ListTreeAsync(Source source, string sha, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Tree.ToList());
    public Task<string?> ReadTextFileAsync(Source source, string sha, string path, CancellationToken ct = default) => Task.FromResult(Files.GetValueOrDefault(path));
    public Task<IReadOnlyList<string>> DiffPathsAsync(Source source, string fromSha, string toSha, CancellationToken ct = default) { DiffCalls++; return Task.FromResult<IReadOnlyList<string>>(Changed.ToList()); }
    public Task<(DateTimeOffset CommittedAt, string Sha)?> LastCommitForPathAsync(Source source, string sha, string path, CancellationToken ct = default) { LastCommitCalls++; return Task.FromResult<(DateTimeOffset, string)?>((DateTimeOffset.UtcNow, sha)); }
    public Task<Uri> GetAuthenticatedCloneUrlAsync(Source source, CancellationToken ct = default) => Task.FromResult(new Uri("https://x-access-token:secret@github.com/o/r.git"));
    public Task<(bool IsPrivate, string DefaultBranch, bool CallerIsOwner)> GetRepoInfoAsync(string owner, string repo, CancellationToken ct = default) => Task.FromResult((true, "main", true));
}

internal sealed class FakeRunner : IBuildRunner
{
    public List<BuildRequest> Started { get; } = [];
    public bool Fail { get; set; }
    public string Version { get; set; } = "builder-v1";
    public Task<string> GetBuilderVersionAsync(CancellationToken ct = default) => Task.FromResult(Version);
    public Task<string> StartAsync(BuildRequest request, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("runner down");
        Started.Add(request);
        return Task.FromResult("exec-" + Started.Count);
    }
}

internal sealed class FakeArtifacts : IArtifactStore
{
    public List<(string Slug, string Build)> Deleted { get; } = [];
    public Task<ArtifactObject?> OpenSiteFileAsync(string deckSlug, string buildId, string relativePath, CancellationToken ct = default) => Task.FromResult<ArtifactObject?>(null);
    public Task<ArtifactObject?> OpenArtifactAsync(string deckSlug, string buildId, ArtifactKind kind, CancellationToken ct = default) => Task.FromResult<ArtifactObject?>(null);
    public Task<Uri> CreateUploadUriAsync(string deckSlug, string buildId, TimeSpan lifetime, CancellationToken ct = default) => Task.FromResult(new Uri($"https://blob/{deckSlug}/{buildId}?sig=x"));
    public Task DeleteBuildAsync(string deckSlug, string buildId, CancellationToken ct = default) { Deleted.Add((deckSlug, buildId)); return Task.CompletedTask; }
}

internal sealed class FakeTokens : IBuildTokenService
{
    public string Issue(string deckSlug, string buildId, TimeSpan lifetime) => $"tok:{deckSlug}:{buildId}";
    public bool Validate(string token, string deckSlug, string buildId) => token == $"tok:{deckSlug}:{buildId}";
}

public class DeckSyncServiceTests
{
    private readonly InMemorySourceStore _sources = new();
    private readonly InMemoryDeckStore _decks = new();
    private readonly InMemoryBuildStore _builds = new();
    private readonly FakeRepo _repo = new();
    private readonly FakeRunner _runner = new();
    private readonly FakeArtifacts _artifacts = new();
    private readonly BuildService _buildService;
    private readonly DeckSyncService _sync;
    private readonly Source _source = new() { Id = "moaidhathot/slides", Owner = "MoaidHathot", Repo = "Slides", Trusted = true, InstallationId = 1 };

    public DeckSyncServiceTests()
    {
        _buildService = new BuildService(_builds, _decks, _artifacts, _repo, _runner, new FakeTokens(),
            Options.Create(new BuildOptions { PublicBaseUrl = new Uri("https://slides.example.test") }), NullLogger<BuildService>.Instance);
        _sync = new DeckSyncService(_sources, _decks, _repo, _buildService, NullLogger<DeckSyncService>.Instance);
        _repo.Tree.AddRange(["Talks/Agents/slides.md", "Talks/Agents/package.json", "Talks/Intro/main.md", "Talks/Intro/config.yaml", "Old/Async/PITCHME.md"]);
        _repo.Files["Talks/Agents/slides.md"] = "---\ntitle: Agents\n---\n# x";
        _repo.Files["Talks/Intro/main.md"] = "---\ntitle: Intro\n---\n";
    }

    [Fact]
    public async Task First_sync_indexes_all_decks_and_builds_only_buildable_ones()
    {
        var r = await _sync.SyncAsync(_source);

        Assert.Equal(2, r.Added.Count);
        Assert.Equal(2, r.Queued.Count);
        Assert.Equal(2, _runner.Started.Count);
        var decks = await _decks.ListAsync();
        Assert.Contains(decks, d => d.Slug == "slides-agents" && d.Title == "Agents" && d.Kind == DeckKind.Slidev && d.LatestBuildStatus == BuildStatus.Running);
        Assert.Contains(decks, d => d.Slug == "slides-intro" && d.Kind == DeckKind.Presenterm);
        Assert.DoesNotContain(decks, d => d.Slug == "slides-async"); // GitPitch is not indexed
        Assert.All(decks, d => Assert.Equal(Visibility.Private, d.Visibility));

        var req = _runner.Started[0];
        Assert.Equal("https://slides.example.test/api/builds/slides-agents/" + req.Build.Id + "/report", req.CallbackUri.ToString());
        Assert.Equal(TimeSpan.FromMinutes(15), req.Timeout);
    }

    [Fact]
    public async Task Resync_with_same_sha_does_nothing()
    {
        await _sync.SyncAsync(_source);
        var before = _runner.Started.Count;
        var lastCommitCalls = _repo.LastCommitCalls;

        var src = await _sources.GetAsync(_source.Id);
        var r = await _sync.SyncAsync(src!);

        Assert.Empty(r.Added);
        Assert.Empty(r.Queued);
        Assert.Equal(before, _runner.Started.Count);
        Assert.Equal(0, _repo.DiffCalls);
        Assert.Equal(lastCommitCalls, _repo.LastCommitCalls);
    }

    [Fact]
    public async Task New_commit_rebuilds_only_touched_decks_and_preserves_slug_and_settings()
    {
        await _sync.SyncAsync(_source);
        var agents = (await _decks.GetAsync("slides-agents"))!;
        await _decks.UpsertAsync(agents with { Visibility = Visibility.Public, Pinned = true });
        // finish the running build so a new one can be queued
        var first = _runner.Started.Single(s => s.Deck.Slug == "slides-agents");
        Assert.True(await _buildService.CompleteAsync("slides-agents", first.Build.Id, first.CallbackToken, new BuildReport(true, true, true, false, null, null)));

        _repo.Sha = "bbbbbbb0000000000000000000000000000000002";
        _repo.Changed.Add("Talks/Agents/slides.md");
        _repo.Files["Talks/Agents/slides.md"] = "---\ntitle: Agents v2\n---\n";

        var r = await _sync.SyncAsync(await _sources.GetAsync(_source.Id) ?? _source);

        Assert.Equal(["slides-agents"], r.Queued);
        Assert.Equal(1, _repo.DiffCalls);
        var updated = (await _decks.GetAsync("slides-agents"))!;
        Assert.Equal("Agents v2", updated.Title);
        Assert.Equal(Visibility.Public, updated.Visibility);
        Assert.True(updated.Pinned);
        Assert.Equal(first.Build.Id, updated.CurrentBuildId); // still serving the last good build while the new one runs
        Assert.Equal(BuildStatus.Running, updated.LatestBuildStatus);
    }

    [Fact]
    public async Task Webhook_changed_paths_are_used_instead_of_diff()
    {
        await _sync.SyncAsync(_source);
        _repo.Sha = "ccccccc0000000000000000000000000000000003";
        var r = await _sync.SyncAsync(await _sources.GetAsync(_source.Id) ?? _source, changedPaths: ["Talks/Intro/main.md"]);
        Assert.Equal(["slides-intro"], r.Queued);
        Assert.Equal(0, _repo.DiffCalls);
    }

    [Fact]
    public async Task Removed_deck_is_archived_and_restored_when_it_returns()
    {
        await _sync.SyncAsync(_source);
        _repo.Sha = "ddddddd0000000000000000000000000000000004";
        _repo.Tree.RemoveAll(p => p.StartsWith("Talks/Intro/", StringComparison.Ordinal));
        _repo.Changed.Add("Talks/Intro/main.md");

        var r = await _sync.SyncAsync(await _sources.GetAsync(_source.Id) ?? _source);
        Assert.Equal(["slides-intro"], r.Archived);
        Assert.True((await _decks.GetAsync("slides-intro"))!.Archived);
        Assert.DoesNotContain(await _decks.ListAsync(), d => d.Slug == "slides-intro");

        _repo.Sha = "eeeeeee0000000000000000000000000000000005";
        _repo.Tree.AddRange(["Talks/Intro/main.md", "Talks/Intro/config.yaml"]);
        await _sync.SyncAsync(await _sources.GetAsync(_source.Id) ?? _source);
        Assert.False((await _decks.GetAsync("slides-intro"))!.Archived);
    }

    [Fact]
    public async Task Same_folder_names_get_unique_slugs()
    {
        _repo.Tree.AddRange(["A/intro/slides.md", "B/intro/slides.md"]);
        await _sync.SyncAsync(_source);
        var slugs = (await _decks.ListAsync()).Select(d => d.Slug).ToList();
        Assert.Contains("slides-intro", slugs);
        Assert.Contains("slides-intro-2", slugs);
        Assert.Contains("slides-intro-3", slugs);
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
    }

    [Fact]
    public async Task Failed_build_is_not_retried_on_unchanged_resync_but_is_on_force()
    {
        _runner.Fail = true;
        await _sync.SyncAsync(_source);
        var deck = (await _decks.GetAsync("slides-agents"))!;
        Assert.Equal(BuildStatus.Failed, deck.LatestBuildStatus);
        Assert.Null(deck.CurrentBuildId);

        _runner.Fail = false;
        var r = await _sync.SyncAsync(await _sources.GetAsync(_source.Id) ?? _source);
        Assert.Empty(r.Queued);

        r = await _sync.SyncAsync(await _sources.GetAsync(_source.Id) ?? _source, forceRebuild: true);
        Assert.Equal(2, r.Queued.Count);
    }

    [Fact]
    public async Task Completion_requires_valid_token_and_cleans_old_builds()
    {
        await _sync.SyncAsync(_source);
        var req = _runner.Started.Single(s => s.Deck.Slug == "slides-agents");

        Assert.False(await _buildService.CompleteAsync("slides-agents", req.Build.Id, "wrong", new BuildReport(true, true, false, false, null, null)));
        Assert.True(await _buildService.CompleteAsync("slides-agents", req.Build.Id, req.CallbackToken, new BuildReport(true, true, true, false, null, ["w1"])));
        Assert.False(await _buildService.CompleteAsync("slides-agents", req.Build.Id, req.CallbackToken, new BuildReport(true, true, true, false, null, null)), "second report must be rejected");

        var b1 = req.Build.Id;
        var deck = (await _decks.GetAsync("slides-agents"))!;
        Assert.Equal(b1, deck.CurrentBuildId);
        Assert.Equal(["w1"], (await _builds.GetAsync("slides-agents", b1))!.Warnings);

        // two more successful builds => the first one gets deleted, the second kept for rollback
        var src = await _sources.GetAsync(_source.Id) ?? _source;
        var b2 = (await _buildService.QueueAsync(deck, src, "f000000000000000000000000000000000000002", "test", [], default)).Id;
        Assert.True(await _buildService.CompleteAsync("slides-agents", b2, $"tok:slides-agents:{b2}", new BuildReport(true, true, false, false, null, null)));
        Assert.Empty(_artifacts.Deleted);
        var b3 = (await _buildService.QueueAsync((await _decks.GetAsync("slides-agents"))!, src, "f000000000000000000000000000000000000003", "test", [], default)).Id;
        Assert.True(await _buildService.CompleteAsync("slides-agents", b3, $"tok:slides-agents:{b3}", new BuildReport(true, true, false, false, null, null)));
        Assert.Equal([("slides-agents", b1)], _artifacts.Deleted);
        Assert.Equal(b3, (await _decks.GetAsync("slides-agents"))!.CurrentBuildId);
    }

    [Fact]
    public async Task Failed_report_keeps_previous_current_build()
    {
        await _sync.SyncAsync(_source);
        var req = _runner.Started.Single(s => s.Deck.Slug == "slides-agents");
        await _buildService.CompleteAsync("slides-agents", req.Build.Id, req.CallbackToken, new BuildReport(true, true, false, false, null, null));
        var deck = (await _decks.GetAsync("slides-agents"))!;
        var src = await _sources.GetAsync(_source.Id) ?? _source;

        var b2 = (await _buildService.QueueAsync(deck, src, "f000000000000000000000000000000000000002", "test", [], default)).Id;
        await _buildService.CompleteAsync("slides-agents", b2, $"tok:slides-agents:{b2}", new BuildReport(false, false, false, false, "npm ci failed", null));

        deck = (await _decks.GetAsync("slides-agents"))!;
        Assert.Equal(req.Build.Id, deck.CurrentBuildId);
        Assert.Equal(BuildStatus.Failed, deck.LatestBuildStatus);
        Assert.Equal("npm ci failed", (await _builds.GetAsync("slides-agents", b2))!.Error);
    }

    [Fact]
    public async Task Duplicate_queue_for_same_sha_is_collapsed()
    {
        await _sync.SyncAsync(_source);
        var deck = (await _decks.GetAsync("slides-agents"))!;
        var src = await _sources.GetAsync(_source.Id) ?? _source;
        var again = await _buildService.QueueAsync(deck, src, _repo.Sha, "test", [], default);
        Assert.Equal(deck.LatestBuildId, again.Id);
        Assert.Equal(2, _runner.Started.Count);
    }

    [Fact]
    public async Task Stale_running_builds_are_reaped()
    {
        var opts = new BuildOptions { PublicBaseUrl = new Uri("https://x.test"), StaleAfter = TimeSpan.Zero };
        var svc = new BuildService(_builds, _decks, _artifacts, _repo, _runner, new FakeTokens(), Options.Create(opts), NullLogger<BuildService>.Instance);
        var sync = new DeckSyncService(_sources, _decks, _repo, svc, NullLogger<DeckSyncService>.Instance);
        await sync.SyncAsync(_source);
        await Task.Delay(10);
        await svc.ReapStaleAsync();
        Assert.Empty(await _builds.ListActiveAsync());
        Assert.Equal(BuildStatus.Failed, (await _decks.GetAsync("slides-agents"))!.LatestBuildStatus);
    }

    [Fact]
    public async Task Builder_upgrade_rebuilds_decks_built_by_older_builder_and_retries_failed_ones()
    {
        await _sync.SyncAsync(_source);
        // Finish agents successfully; leave intro failed.
        var agents = _runner.Started.Single(s => s.Deck.Slug == "slides-agents");
        var intro = _runner.Started.Single(s => s.Deck.Slug == "slides-intro");
        Assert.True(await _buildService.CompleteAsync("slides-agents", agents.Build.Id, agents.CallbackToken, new BuildReport(true, true, false, false, null, null)));
        Assert.True(await _buildService.CompleteAsync("slides-intro", intro.Build.Id, intro.CallbackToken, new BuildReport(false, false, false, false, "boom", null)));
        Assert.Equal("builder-v1", (await _builds.GetAsync("slides-agents", agents.Build.Id))!.BuilderVersion);

        // Same builder: nothing to do.
        var decks = await _decks.ListAsync();
        Assert.Equal(0, await _buildService.RebuildOutdatedAsync(decks, id => _sources.GetAsync(id)));

        // New builder: both decks are rebuilt (one upgrade, one retry), each exactly once.
        _runner.Version = "builder-v2";
        Assert.Equal(2, await _buildService.RebuildOutdatedAsync(await _decks.ListAsync(), id => _sources.GetAsync(id)));
        Assert.Equal(4, _runner.Started.Count);
        Assert.All(await _builds.ListActiveAsync(), b => Assert.Equal("builder-v2", b.BuilderVersion));
        Assert.Equal("builder-upgrade", (await _builds.ListActiveAsync()).First().TriggeredBy);

        // While those builds are active a second pass must not queue duplicates.
        Assert.Equal(0, await _buildService.RebuildOutdatedAsync(await _decks.ListAsync(), id => _sources.GetAsync(id)));
    }
}
