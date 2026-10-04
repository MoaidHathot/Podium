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
public sealed class MaintenanceService(IServiceScopeFactory scopes, SyncQueue queue, IOptions<PodiumOptions> options, IOptions<BuilderOptions> builderOptions, ILogger<MaintenanceService> log) : BackgroundService
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

        await RunSafely("installation discovery", async ct =>
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<InstallationDiscovery>().DiscoverAsync(ct);
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
}
