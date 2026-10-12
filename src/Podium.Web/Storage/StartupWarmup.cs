using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;

namespace Podium.Web.Storage;

/// <summary>
/// Does the first request's slow, one-off work right after the server starts listening, so the request itself does
/// not: creates every table (concurrently; each costs a round-trip plus, in Azure, a managed-identity token) and
/// loads the data-protection key ring (blob download and, in production, a Key Vault unwrap) by protecting one byte.
/// The framework normally preloads the key ring in a start-up filter, before Kestrel listens; Program.cs removes that
/// filter so the readiness probe passes first and this warm-up runs alongside the table bootstrap instead.
/// Failures are logged, never fatal: every path here is retried by the first real request that needs it.
/// </summary>
public sealed class StartupWarmup(IHostApplicationLifetime lifetime, IServiceProvider services, ILogger<StartupWarmup> log) : BackgroundService
{
    private readonly TaskCompletionSource<WarmupResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the warm-up has finished (or was skipped), with what it did; for tests and diagnostics.</summary>
    public Task<WarmupResult> Completed => _done.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var stop = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        try { await started.Task; } catch (OperationCanceledException) { _done.TrySetResult(new WarmupResult(false, 0, false, 0)); return; }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tables = services.GetService<TableClients>();
        var tableTask = tables is null ? Task.CompletedTask : tables.EnsureAllAsync(cts.Token);
        var keyRingTask = Task.Run(() =>
        {
            // Protect, not just CreateProtector: creating a protector is lazy, protecting a payload loads the ring.
            services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Podium.StartupWarmup").Protect([1]);
        }, cts.Token);

        var tablesOk = await Observe("table bootstrap", tableTask);
        var keyRingOk = await Observe("data-protection key ring", keyRingTask);
        watch.Stop();
        log.LogInformation("Warm-up finished in {Ms} ms: tables {Tables}, key ring {KeyRing}", watch.ElapsedMilliseconds, tables is null ? "n/a" : tablesOk ? $"{TableClients.KnownTables.Length} ready" : "failed", keyRingOk ? "loaded" : "failed");
        _done.TrySetResult(new WarmupResult(tables is not null && tablesOk, tables is null ? 0 : TableClients.KnownTables.Length, keyRingOk, watch.ElapsedMilliseconds));
    }

    private async Task<bool> Observe(string what, Task task)
    {
        try { await task; return true; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Warm-up: {What} failed; the first request that needs it will retry", what);
            return false;
        }
    }
}

public sealed record WarmupResult(bool TablesReady, int TableCount, bool KeyRingLoaded, long ElapsedMs);
