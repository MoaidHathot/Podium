using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.Services;
using Podium.Web.Configuration;
using Podium.Web.GitHub;

namespace Podium.Web.Builds;

/// <summary>
/// Periodic maintenance: polls sources that cannot deliver webhooks, safety-net polls the rest, reaps stale builds,
/// and refreshes App installations on startup.
/// </summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, SyncQueue queue, IOptions<PodiumOptions> options, IOptions<BuilderOptions> builderOptions, ILogger<MaintenanceService> log, Podium.Web.Sync.AudienceService audience) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the host a moment to finish starting before hitting external services.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { return; }

        // One-off data migrations, guarded by markers in the settings table so they run exactly once per deployment.
        await RunSafely("migrations", async ct =>
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
            if (await settings.GetAsync("migration:deckviews-backfill", ct) is null)
            {
                if (scope.ServiceProvider.GetRequiredService<IViewHistoryStore>() is Podium.Web.Storage.TableViewHistoryStore views)
                {
                    var copied = await views.BackfillDeckViewsAsync(ct);
                    log.LogInformation("Backfilled {Count} view(s) into the per-deck analytics table", copied);
                }
                await settings.SetAsync("migration:deckviews-backfill", DateTimeOffset.UtcNow.ToString("o"), ct);
            }
        }, stoppingToken);

        await RunSafely("search index", async ct =>
        {
            using var scope = scopes.CreateScope();
            var index = scope.ServiceProvider.GetRequiredService<Podium.Core.Services.DeckSearchIndex>();
            foreach (var d in await scope.ServiceProvider.GetRequiredService<IDeckStore>().ListAsync(includeArchived: false, ct)) await index.RefreshAsync(d, ct);
            log.LogInformation("Search index ready: {Count} deck(s)", index.DeckCount);
        }, stoppingToken);

        await RunSafely("installation discovery", async ct =>
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<InstallationDiscovery>().DiscoverAsync(ct);
        }, stoppingToken);

        // Sync rules that derive deck records from the repository (titles, tags, talk membership...) change with
        // Podium itself, not with the repository. After a deploy that bumps the rules version, every source is synced
        // once at the current commit so existing records pick the new rules up without waiting for a push or a poll.
        // The diff is empty, so nothing is rebuilt.
        await RunSafely("sync rules", async ct =>
        {
            using var scope = scopes.CreateScope();
            var queued = await ResyncAfterRulesChangeAsync(scope.ServiceProvider.GetRequiredService<ISettingsStore>(), scope.ServiceProvider.GetRequiredService<ISourceStore>(), queue.TryEnqueue, ct);
            if (queued > 0) log.LogInformation("Sync rules {Version}: queued a re-sync of {Count} source(s)", Podium.Core.Services.DeckSyncService.RulesVersion, queued);
        }, stoppingToken);

        var lastUpgradeCheck = DateTimeOffset.MinValue;
        var lastRetentionSweep = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSafely("reaper", async ct =>
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<BuildService>().ReapStaleAsync(ct);
            }, stoppingToken);

            // Reactions, questions and polls of live rooms are persisted once a minute so a restart loses little.
            await RunSafely("audience snapshot", ct => audience.SnapshotAllAsync(ct), stoppingToken);

            // Live sessions whose presenter vanished, overran or hit the cap end by themselves (see LiveSessionOptions).
            await RunSafely("session sweep", async ct =>
            {
                using var scope = scopes.CreateScope();
                var hub = scope.ServiceProvider.GetRequiredService<Podium.Web.Sync.SyncHub>();
                var access = scope.ServiceProvider.GetRequiredService<Podium.Web.Serving.DeckAccessService>();
                var sessions = scope.ServiceProvider.GetRequiredService<Podium.Core.Services.SessionService>();
                var before = await scope.ServiceProvider.GetRequiredService<ISessionStore>().ListLiveAsync(ct);
                var ended = await sessions.SweepAsync(slug => hub.Presence(slug).Presenters, ct);
                if (ended > 0) foreach (var s in before) access.Invalidate(s.DeckSlug);
            }, stoppingToken);

            // Builds waiting for a free slot (see BuildOptions.MaxConcurrentBuilds); normally dispatched as builds
            // finish, this is the safety net for lost callbacks and restarts.
            await RunSafely("dispatch", async ct =>
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<BuildService>().DispatchPendingAsync(ct);
            }, stoppingToken);

            // Builder upgrades roll out automatically: decks built by an older builder are rebuilt. Checked at start
            // (after the first tick, so discovery has had a chance to register sources) and hourly thereafter.
            if (builderOptions.Value.AutoRebuildOnUpgrade && DateTimeOffset.UtcNow - lastUpgradeCheck > TimeSpan.FromHours(1))
            {
                lastUpgradeCheck = DateTimeOffset.UtcNow;
                await RunSafely("builder upgrade check", async ct =>
                {
                    using var scope = scopes.CreateScope();
                    var sp = scope.ServiceProvider;
                    var decks = await sp.GetRequiredService<IDeckStore>().ListAsync(includeArchived: false, ct);
                    var sources = sp.GetRequiredService<ISourceStore>();
                    await sp.GetRequiredService<BuildService>().RebuildOutdatedAsync(decks, id => sources.GetAsync(id, ct), ct);
                }, stoppingToken);
            }

            // Storage hygiene, once a day: trim build history/artifacts per deck and purge decks archived long ago.
            if (DateTimeOffset.UtcNow - lastRetentionSweep > TimeSpan.FromHours(24))
            {
                lastRetentionSweep = DateTimeOffset.UtcNow;
                await RunSafely("retention sweep", async ct =>
                {
                    using var scope = scopes.CreateScope();
                    var sp = scope.ServiceProvider;
                    var buildService = sp.GetRequiredService<BuildService>();
                    var buildOptions = sp.GetRequiredService<IOptions<BuildOptions>>().Value;
                    var deckStore = sp.GetRequiredService<IDeckStore>();
                    foreach (var deck in await deckStore.ListAsync(includeArchived: true, ct))
                    {
                        if (deck.Archived && DateTimeOffset.UtcNow - deck.UpdatedAt > buildOptions.ArchivedPurgeAfter)
                        {
                            await buildService.PurgeDeckAsync(deck, ct);
                            await deckStore.UpsertAsync(deck with { CurrentBuildId = null, LatestSuccessfulBuildId = null, PinnedBuildId = null, CurrentHasPdf = false, CurrentHasPptx = false, CurrentHasThumbnail = false, CurrentHasPublicSite = false, CurrentHasNotes = false, CurrentHasText = false, CurrentHasSlideSheet = false, CurrentSlideCount = 0 }, ct);
                        }
                        else if (!deck.Archived)
                            await buildService.ApplyRetentionAsync(deck, ct);
                    }
                }, stoppingToken);
            }

            await RunSafely("poll", async ct =>
            {
                using var scope = scopes.CreateScope();
                var sources = await scope.ServiceProvider.GetRequiredService<ISourceStore>().ListAsync(ct);
                var now = DateTimeOffset.UtcNow;
                foreach (var s in sources)
                {
                    var interval = s.InstallationId is null ? options.Value.ExternalPollInterval : options.Value.InstalledPollInterval;
                    if (s.LastScannedAt is null || now - s.LastScannedAt > interval)
                        queue.TryEnqueue(new SyncJob(s.Id, null, false, "poll"));
                }
            }, stoppingToken);
        }
    }

    private async Task RunSafely(string name, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try { await action(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { log.LogError(ex, "Maintenance task {Task} failed", name); }
    }

    /// <summary>
    /// Queues one sync per source when the deployed sync rules differ from the version recorded in settings, and
    /// records the deployed version. Returns how many syncs were queued (0 when the rules are unchanged).
    /// </summary>
    public static async Task<int> ResyncAfterRulesChangeAsync(ISettingsStore settings, ISourceStore sources, Func<SyncJob, bool> enqueue, CancellationToken ct)
    {
        const string key = "sync:rules-version";
        if (await settings.GetAsync(key, ct) == Podium.Core.Services.DeckSyncService.RulesVersion) return 0;
        var all = await sources.ListAsync(ct);
        var queued = 0;
        foreach (var s in all) if (enqueue(new SyncJob(s.Id, null, false, "rules-upgrade"))) queued++;
        await settings.SetAsync(key, Podium.Core.Services.DeckSyncService.RulesVersion, ct);
        return queued;
    }
}
