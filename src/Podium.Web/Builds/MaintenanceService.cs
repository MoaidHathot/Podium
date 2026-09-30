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
public sealed class MaintenanceService(IServiceScopeFactory scopes, SyncQueue queue, IOptions<PodiumOptions> options, ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the host a moment to finish starting before hitting external services.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { return; }

        await RunSafely("installation discovery", async ct =>
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<InstallationDiscovery>().DiscoverAsync(ct);
        }, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSafely("reaper", async ct =>
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<BuildService>().ReapStaleAsync(ct);
            }, stoppingToken);

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
