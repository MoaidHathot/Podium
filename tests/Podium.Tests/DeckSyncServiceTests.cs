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
    public Task<(bool IsPrivate, string DefaultBranch, bool CallerIsOwner, long RepoId)> GetRepoInfoAsync(string owner, string repo, CancellationToken ct = default) => Task.FromResult((true, "main", true, 4242L));
}

internal sealed class FakeRunner : IBuildRunner
{
    public List<BuildRequest> Started { get; } = [];
    public bool Fail { get; set; }
    public string Version { get; set; } = "builder-v1";
    /// <summary>Simulates the seconds a real job start takes, to expose dispatch races.</summary>
    public TimeSpan StartDelay { get; set; }
    public Task<string> GetBuilderVersionAsync(CancellationToken ct = default) => Task.FromResult(Version);
    public async Task<string> StartAsync(BuildRequest request, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("runner down");
        if (StartDelay > TimeSpan.Zero) await Task.Delay(StartDelay, ct);
        lock (Started) { Started.Add(request); return "exec-" + Started.Count; }
    }
}

internal sealed class FakeArtifacts : IArtifactStore
{
    public List<(string Slug, string Build)> Deleted { get; } = [];
    public Task<ArtifactObject?> OpenSiteFileAsync(string deckSlug, string buildId, string relativePath, CancellationToken ct = default, string variant = "site") => Task.FromResult<ArtifactObject?>(null);
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
            Options.Create(new BuildOptions { PublicBaseUrl = new Uri("https://slides.example.test") }), NullLogger<BuildService>.Instance, _sources);
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
    public async Task Podium_yml_seeds_visibility_once_but_reapplies_declarative_fields_and_alias()
    {
        _repo.Tree.AddRange(["Talks/Agents/.podium.yml"]);
        _repo.Files["Talks/Agents/.podium.yml"] = "title: Agents (config)\nalias: agents\ntags: [ai]\nvisibility: public\nexportPptx: true\nstripNotes: false\nnpmScripts: false\n";
        await _sync.SyncAsync(_source);
        var deck = (await _decks.GetAsync("slides-agents"))!;
        Assert.Equal("Agents (config)", deck.Title);
        Assert.Equal("agents", deck.Alias);
        Assert.False(deck.NpmScripts);
        Assert.Equal(["ai"], deck.Tags);
        Assert.Equal(Visibility.Public, deck.Visibility);
        Assert.True(deck.ExportPptx);
        Assert.False(deck.StripNotesForViewers);
        Assert.Same(deck, await _decks.GetByAliasAsync("agents"));

        // Owner flips visibility in the UI; the next sync (file unchanged but deck touched) must not undo it, while
        // declarative fields keep following the file.
        await _decks.UpsertAsync(deck with { Visibility = Visibility.Private });
        _repo.Sha = "abcabca0000000000000000000000000000000002";
        _repo.Changed.Add("Talks/Agents/.podium.yml");
        _repo.Files["Talks/Agents/.podium.yml"] = "title: Agents v2\nalias: agents\nvisibility: public\n";
        await _sync.SyncAsync(await _sources.GetAsync(_source.Id) ?? _source);
        deck = (await _decks.GetAsync("slides-agents"))!;
        Assert.Equal("Agents v2", deck.Title);
        Assert.Equal(Visibility.Private, deck.Visibility);
    }

    [Fact]
    public async Task Alias_that_clashes_with_another_deck_slug_is_ignored()
    {
        _repo.Tree.AddRange(["Talks/Agents/.podium.yml"]);
        _repo.Files["Talks/Agents/.podium.yml"] = "alias: slides-intro\n"; // another deck's slug
        await _sync.SyncAsync(_source);
        Assert.Null((await _decks.GetAsync("slides-agents"))!.Alias);
    }

    [Fact]
    public async Task Npm_scripts_follow_podium_yml_for_trusted_sources_only_and_reach_the_builder_env()
    {
        _repo.Tree.AddRange(["Talks/Agents/.podium.yml"]);
        _repo.Files["Talks/Agents/.podium.yml"] = "npmScripts: true\n";
        await _sync.SyncAsync(_source);
        Assert.True((await _decks.GetAsync("slides-agents"))!.NpmScripts);
        var req = _runner.Started.Single(s => s.Deck.Slug == "slides-agents");
        Assert.Equal("1", Podium.Web.Builds.BuilderEnvironment.For(req)["PODIUM_NPM_SCRIPTS"]);

        // The same file in a repository the owner does not control is ignored.
        var untrusted = _source with { Id = "stranger/talks", Owner = "stranger", Repo = "talks", Trusted = false };
        await _sources.UpsertAsync(untrusted);
        await _sync.SyncAsync(untrusted);
        var foreign = (await _decks.ListBySourceAsync(untrusted.Id)).Single(d => d.Path == "Talks/Agents");
        Assert.False(foreign.NpmScripts);
        Assert.Equal("0", Podium.Web.Builds.BuilderEnvironment.For(_runner.Started.Single(s => s.Deck.Slug == foreign.Slug))["PODIUM_NPM_SCRIPTS"]);
    }

    [Fact]
    public async Task Untrusted_sources_ignore_podium_yml()
    {
        _repo.Tree.AddRange(["Talks/Agents/.podium.yml"]);
        _repo.Files["Talks/Agents/.podium.yml"] = "visibility: public\nalias: agents\n";
        await _sync.SyncAsync(_source with { Trusted = false });
        var deck = (await _decks.GetAsync("slides-agents"))!;
        Assert.Equal(Visibility.Private, deck.Visibility);
        Assert.Null(deck.Alias);
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
    public async Task Two_powerpoint_files_in_one_folder_are_separate_decks_and_only_the_changed_one_rebuilds()
    {
        _repo.Tree.AddRange(["Decks/keynote.pptx", "Decks/keynote.pdf", "Decks/workshop.pptx"]);
        await _sync.SyncAsync(_source);
        var decks = await _decks.ListAsync();
        var keynote = Assert.Single(decks, d => d.Slug == "slides-keynote");
        var workshop = Assert.Single(decks, d => d.Slug == "slides-workshop");
        Assert.Equal(DeckKind.PowerPoint, keynote.Kind);
        Assert.Equal("keynote.pptx", keynote.Entry);
        Assert.Equal("Decks", keynote.Path);
        Assert.Equal("workshop", workshop.Title);
        Assert.True(keynote.ExportPdf);

        // Finish both builds, then change only the keynote's committed PDF export.
        foreach (var slug in new[] { "slides-keynote", "slides-workshop" })
        {
            var req = _runner.Started.Single(s => s.Deck.Slug == slug);
            Assert.True(await _buildService.CompleteAsync(slug, req.Build.Id, req.CallbackToken, new BuildReport(true, true, true, true, null, null, HasThumbnail: true)));
        }
        Assert.True((await _decks.GetAsync("slides-keynote"))!.CurrentHasThumbnail);
        Assert.True((await _decks.GetAsync("slides-keynote"))!.CurrentHasPptx);

        _repo.Sha = "abcabca0000000000000000000000000000000009";
        _repo.Changed.Add("Decks/keynote.pdf");
        var r = await _sync.SyncAsync(await _sources.GetAsync(_source.Id) ?? _source);
        Assert.Equal(["slides-keynote"], r.Queued);
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

        // two more successful builds => the first one loses its artifacts, the second is kept as the rollback target
        var src = await _sources.GetAsync(_source.Id) ?? _source;
        var b2 = (await _buildService.QueueAsync(deck, src, "f000000000000000000000000000000000000002", "test", [], default)).Id;
        Assert.True(await _buildService.CompleteAsync("slides-agents", b2, $"tok:slides-agents:{b2}", new BuildReport(true, true, false, false, null, null)));
        Assert.Empty(_artifacts.Deleted);
        var b3 = (await _buildService.QueueAsync((await _decks.GetAsync("slides-agents"))!, src, "f000000000000000000000000000000000000003", "test", [], default)).Id;
        Assert.True(await _buildService.CompleteAsync("slides-agents", b3, $"tok:slides-agents:{b3}", new BuildReport(true, true, false, false, null, null)));
        Assert.Equal([("slides-agents", b1)], _artifacts.Deleted);
        Assert.Equal(b3, (await _decks.GetAsync("slides-agents"))!.CurrentBuildId);
        Assert.False((await _builds.GetAsync("slides-agents", b1))!.HasSite, "artifact-less build must be marked so the UI does not offer to serve it");
        Assert.True((await _builds.GetAsync("slides-agents", b2))!.HasSite);
    }

    [Fact]
    public async Task Frozen_deck_keeps_serving_pinned_build_until_unfrozen_and_rollback_serves_older_build()
    {
        await _sync.SyncAsync(_source);
        var req = _runner.Started.Single(s => s.Deck.Slug == "slides-agents");
        Assert.True(await _buildService.CompleteAsync("slides-agents", req.Build.Id, req.CallbackToken, new BuildReport(true, true, true, false, null, null)));
        var b1 = req.Build.Id;
        var src = await _sources.GetAsync(_source.Id) ?? _source;

        // Freeze on b1, then a new build b2 succeeds: still serving b1, b2 recorded as latest success.
        var frozen = await _buildService.SetFrozenAsync("slides-agents", true);
        Assert.Equal(b1, frozen!.PinnedBuildId);
        var b2 = (await _buildService.QueueAsync(frozen, src, "f000000000000000000000000000000000000002", "test", [], default)).Id;
        Assert.True(await _buildService.CompleteAsync("slides-agents", b2, $"tok:slides-agents:{b2}", new BuildReport(true, true, false, false, null, null)));
        var deck = (await _decks.GetAsync("slides-agents"))!;
        Assert.Equal(b1, deck.CurrentBuildId);
        Assert.True(deck.CurrentHasPdf);
        Assert.Equal(b2, deck.LatestSuccessfulBuildId);
        Assert.Empty(_artifacts.Deleted); // both builds retained: pinned + latest

        // Unfreeze: catches up to b2.
        deck = (await _buildService.SetFrozenAsync("slides-agents", false))!;
        Assert.Null(deck.PinnedBuildId);
        Assert.Equal(b2, deck.CurrentBuildId);
        Assert.False(deck.CurrentHasPdf);

        // Rollback to b1 (not frozen): served now, but the next success replaces it again.
        deck = (await _buildService.ServeBuildAsync("slides-agents", b1, freeze: false))!;
        Assert.Equal(b1, deck.CurrentBuildId);
        var b3 = (await _buildService.QueueAsync(deck, src, "f000000000000000000000000000000000000003", "test", [], default)).Id;
        Assert.True(await _buildService.CompleteAsync("slides-agents", b3, $"tok:slides-agents:{b3}", new BuildReport(true, true, false, false, null, null)));
        Assert.Equal(b3, (await _decks.GetAsync("slides-agents"))!.CurrentBuildId);

        // Serving a failed or artifact-less build is refused.
        Assert.Null(await _buildService.ServeBuildAsync("slides-agents", "does-not-exist", false));
    }

    [Fact]
    public async Task Retention_caps_build_records_and_keeps_served_pinned_and_rollback_artifacts()
    {
        await _sync.SyncAsync(_source);
        var req = _runner.Started.Single(s => s.Deck.Slug == "slides-agents");
        Assert.True(await _buildService.CompleteAsync("slides-agents", req.Build.Id, req.CallbackToken, new BuildReport(true, true, false, false, null, null)));
        var src = await _sources.GetAsync(_source.Id) ?? _source;
        var ids = new List<string> { req.Build.Id };
        for (var i = 2; i <= 30; i++)
        {
            var deck = (await _decks.GetAsync("slides-agents"))!;
            var id = (await _buildService.QueueAsync(deck, src, $"f{i:D39}", "test", [], default)).Id;
            Assert.True(await _buildService.CompleteAsync("slides-agents", id, $"tok:slides-agents:{id}", new BuildReport(i % 7 != 0, i % 7 != 0, false, false, i % 7 == 0 ? "boom" : null, null)));
            ids.Add(id);
        }
        var history = await _builds.ListForDeckAsync("slides-agents", 500);
        Assert.Equal(25, history.Count);
        Assert.Equal(ids.Skip(5), history.Select(b => b.Id).Reverse()); // newest-first ordering follows creation order
        var final = (await _decks.GetAsync("slides-agents"))!;
        var withArtifacts = history.Where(b => b.HasSite).Select(b => b.Id).ToList();
        Assert.Equal(2, withArtifacts.Count); // served + rollback
        Assert.Contains(final.CurrentBuildId, withArtifacts);
        Assert.DoesNotContain(_artifacts.Deleted, d => d.Build == final.CurrentBuildId);
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
    public async Task Manual_rebuild_supersedes_a_stuck_active_build()
    {
        await _sync.SyncAsync(_source);
        var deck = (await _decks.GetAsync("slides-agents"))!;
        var src = await _sources.GetAsync(_source.Id) ?? _source;
        var stuck = deck.LatestBuildId!;

        var fresh = await _buildService.QueueAsync(deck, src, _repo.Sha, "manual", [], default, supersedeActive: true);
        Assert.NotEqual(stuck, fresh.Id);
        Assert.Equal(BuildStatus.Cancelled, (await _builds.GetAsync("slides-agents", stuck))!.Status);
        Assert.Equal(3, _runner.Started.Count);
        // The cancelled build must not be able to report over the new one.
        var old = _runner.Started.Single(s => s.Build.Id == stuck);
        Assert.False(await _buildService.CompleteAsync("slides-agents", stuck, old.CallbackToken, new BuildReport(true, true, false, false, null, null)));
        Assert.Equal(fresh.Id, (await _decks.GetAsync("slides-agents"))!.LatestBuildId);
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
    public async Task Build_report_extras_are_recorded_and_annotations_are_capped_and_truncated()
    {
        await _sync.SyncAsync(_source);
        var started = _runner.Started.Single(s => s.Deck.Slug == "slides-agents");
        var annotations = Enumerable.Range(0, 80).Select(i => new BuildAnnotation(new string('p', 1000), -5, AnnotationLevel.Warning, new string('m', 2000))).ToList();
        var report = new BuildReport(true, true, true, false, null, null, HasThumbnail: true, HasPublicSite: true, HasNotes: true, HasText: true, HasSlideSheet: true, SlideCount: 999999, Annotations: annotations);
        Assert.True(await _buildService.CompleteAsync("slides-agents", started.Build.Id, started.CallbackToken, report));

        var build = (await _builds.GetAsync("slides-agents", started.Build.Id))!;
        Assert.True(build.HasNotes && build.HasText && build.HasSlideSheet);
        Assert.Equal(10000, build.SlideCount);
        Assert.Equal(50, build.Annotations.Count);
        Assert.All(build.Annotations, a => { Assert.Equal(300, a.Path.Length); Assert.Equal(500, a.Message.Length); Assert.Equal(1, a.Line); });

        var deck = (await _decks.GetAsync("slides-agents"))!;
        Assert.True(deck.CurrentHasNotes && deck.CurrentHasText && deck.CurrentHasSlideSheet);
        Assert.Equal(10000, deck.CurrentSlideCount);
    }

    [Fact]
    public async Task Document_dates_from_the_report_become_the_deck_s_saved_date_when_plausible()
    {
        await _sync.SyncAsync(_source);
        var started = _runner.Started.Single(s => s.Deck.Slug == "slides-agents");
        var authored = new DateTimeOffset(2019, 10, 30, 7, 51, 0, TimeSpan.Zero);
        var report = new BuildReport(true, true, true, false, null, null, AuthoredAt: authored, DocumentCreatedAt: new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(await _buildService.CompleteAsync("slides-agents", started.Build.Id, started.CallbackToken, report));

        var build = (await _builds.GetAsync("slides-agents", started.Build.Id))!;
        Assert.Equal(authored, build.AuthoredAt);
        Assert.Null(build.DocumentCreatedAt); // 1980: a template or an unset clock, not a creation date
        var deck = (await _decks.GetAsync("slides-agents"))!;
        Assert.Equal(authored, deck.AuthoredAt);
        Assert.Equal(authored, deck.SavedAt); // the document's own date beats the commit that brought it into the repository
        Assert.NotEqual(deck.LastCommitAt, deck.SavedAt);

        // A report without dates (source decks) leaves the saved date at the last commit.
        var plain = (await _decks.GetAsync("slides-agents-lightning")) ?? (await _decks.ListAsync()).First(d => d.Slug != "slides-agents");
        Assert.Null(plain.AuthoredAt);
        Assert.Equal(plain.LastCommitAt ?? plain.UpdatedAt, plain.SavedAt);
    }

    [Fact]
    public async Task Concurrency_cap_holds_builds_in_the_queue_and_dispatches_as_slots_free_up()
    {
        var opts = new BuildOptions { PublicBaseUrl = new Uri("https://x.test"), MaxConcurrentBuilds = 1 };
        var svc = new BuildService(_builds, _decks, _artifacts, _repo, _runner, new FakeTokens(), Options.Create(opts), NullLogger<BuildService>.Instance, _sources);
        await _sources.UpsertAsync(_source);
        var a = new Deck { Slug = "cap-a", SourceId = _source.Id, Path = "a", Entry = "slides.md", Kind = DeckKind.Slidev };
        var b = new Deck { Slug = "cap-b", SourceId = _source.Id, Path = "b", Entry = "slides.md", Kind = DeckKind.Slidev };
        await _decks.UpsertAsync(a); await _decks.UpsertAsync(b);

        var first = await svc.QueueAsync(a, _source, "1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "test", []);
        var second = await svc.QueueAsync(b, _source, "2222222aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "test", []);
        Assert.Equal(BuildStatus.Running, first.Status);
        Assert.Equal(BuildStatus.Queued, second.Status);
        Assert.Null(second.RunnerExecutionId);
        Assert.Single(_runner.Started);

        // A tick while the slot is taken changes nothing; a build that has waited a long time is not "stale" either
        // (only runs that never reported back are), so a long queue is never reaped.
        Assert.Equal(0, await svc.DispatchPendingAsync());
        await _builds.UpsertAsync(second with { QueuedAt = DateTimeOffset.UtcNow.AddHours(-3) });
        var reaper = new BuildService(_builds, _decks, _artifacts, _repo, _runner, new FakeTokens(), Options.Create(new BuildOptions { PublicBaseUrl = new Uri("https://x.test"), StaleAfter = TimeSpan.FromHours(1), MaxConcurrentBuilds = 1 }), NullLogger<BuildService>.Instance, _sources);
        await reaper.ReapStaleAsync();
        Assert.Equal(BuildStatus.Queued, (await _builds.GetAsync("cap-b", second.Id))!.Status);
        Assert.Equal(BuildStatus.Running, (await _builds.GetAsync("cap-a", first.Id))!.Status);

        // Finishing the first build starts the second.
        var started = _runner.Started.Single();
        Assert.True(await svc.CompleteAsync("cap-a", first.Id, started.CallbackToken, new BuildReport(true, true, false, false, null, null)));
        Assert.Equal(2, _runner.Started.Count);
        Assert.Equal(BuildStatus.Running, (await _builds.GetAsync("cap-b", second.Id))!.Status);
        Assert.Equal(BuildStatus.Running, (await _decks.GetAsync("cap-b"))!.LatestBuildStatus);
    }

    [Fact]
    public async Task Overlapping_dispatches_start_a_queued_build_exactly_once()
    {
        // A finished-build callback and the maintenance tick used to dispatch concurrently; both saw the same queued
        // build before its runner id was stored (starting a job takes seconds) and started it twice.
        var opts = new BuildOptions { PublicBaseUrl = new Uri("https://x.test"), MaxConcurrentBuilds = 1 };
        var svc = new BuildService(_builds, _decks, _artifacts, _repo, _runner, new FakeTokens(), Options.Create(opts), NullLogger<BuildService>.Instance, _sources);
        await _sources.UpsertAsync(_source);
        var a = new Deck { Slug = "race-a", SourceId = _source.Id, Path = "a", Entry = "slides.md", Kind = DeckKind.Slidev };
        var b = new Deck { Slug = "race-b", SourceId = _source.Id, Path = "b", Entry = "slides.md", Kind = DeckKind.Slidev };
        await _decks.UpsertAsync(a); await _decks.UpsertAsync(b);
        var first = await svc.QueueAsync(a, _source, "1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "test", []);
        var second = await svc.QueueAsync(b, _source, "2222222aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "test", []);
        Assert.Equal(BuildStatus.Queued, second.Status);

        // Free the slot without dispatching (as a lost callback would), then dispatch from several places at once.
        await _builds.UpsertAsync(first with { Status = BuildStatus.Succeeded, FinishedAt = DateTimeOffset.UtcNow });
        _runner.StartDelay = TimeSpan.FromMilliseconds(150);
        var ticks = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => svc.DispatchPendingAsync())));
        Assert.Equal(1, ticks.Sum());
        Assert.Equal(1, _runner.Started.Count(s => s.Build.Id == second.Id));
        var stored = (await _builds.GetAsync("race-b", second.Id))!;
        Assert.Equal(BuildStatus.Running, stored.Status);
        Assert.StartsWith("exec-", stored.RunnerExecutionId);

        // A later tick finds nothing to do.
        _runner.StartDelay = TimeSpan.Zero;
        Assert.Equal(0, await svc.DispatchPendingAsync());
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

        // The agents rebuild fails on the new builder: the deck keeps serving its v1 build, and the failed v2 attempt
        // counts as "tried" - later checks must not queue it again and again (that would loop on every check).
        var agents2 = _runner.Started.Last(s => s.Deck.Slug == "slides-agents");
        Assert.True(await _buildService.CompleteAsync("slides-agents", agents2.Build.Id, agents2.CallbackToken, new BuildReport(false, false, false, false, "broken on v2", null)));
        var intro2 = _runner.Started.Last(s => s.Deck.Slug == "slides-intro");
        Assert.True(await _buildService.CompleteAsync("slides-intro", intro2.Build.Id, intro2.CallbackToken, new BuildReport(true, true, false, false, null, null)));
        var served = (await _decks.GetAsync("slides-agents"))!;
        Assert.Equal(agents.Build.Id, served.CurrentBuildId);
        Assert.Equal(agents2.Build.Id, served.LatestBuildId);
        Assert.Equal(0, await _buildService.RebuildOutdatedAsync(await _decks.ListAsync(), id => _sources.GetAsync(id)));
        Assert.Equal(4, _runner.Started.Count);

        // A further builder change retries it once more.
        _runner.Version = "builder-v3";
        Assert.Equal(2, await _buildService.RebuildOutdatedAsync(await _decks.ListAsync(), id => _sources.GetAsync(id)));
    }
}
