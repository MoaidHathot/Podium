using System.Reflection;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Podium.Web.Builds;
using Podium.Web.Storage;

namespace Podium.Tests.Web;

/// <summary>Table bootstrap: one creation per table, concurrent across tables, retried after a failure.</summary>
public sealed class TableClientsTests
{
    private static TableClient Client(string name) => new("UseDevelopmentStorage=true", name);

    [Fact]
    public async Task Concurrent_first_touches_create_a_table_once()
    {
        var created = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tables = new TableClients(async name => { Interlocked.Increment(ref created); await gate.Task; return Client(name); });
        var callers = Enumerable.Range(0, 32).Select(_ => tables.GetAsync("decks", CancellationToken.None).AsTask()).ToArray();
        Assert.DoesNotContain(callers, t => t.IsCompleted);
        gate.SetResult();
        var clients = await Task.WhenAll(callers);
        Assert.Equal(1, created);
        Assert.All(clients, c => Assert.Same(clients[0], c));
        // Already-created tables answer synchronously.
        var again = tables.GetAsync("decks", CancellationToken.None);
        Assert.True(again.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Tables_bootstrap_independently_rather_than_behind_one_lock()
    {
        var inFlight = 0; var peak = 0;
        var tables = new TableClients(async name =>
        {
            var now = Interlocked.Increment(ref inFlight);
            while (true) { var seen = peak; if (now <= seen || Interlocked.CompareExchange(ref peak, now, seen) == seen) break; }
            await Task.Delay(40);
            Interlocked.Decrement(ref inFlight);
            return Client(name);
        });
        await tables.EnsureAllAsync(CancellationToken.None);
        Assert.True(peak >= 4, $"peak concurrency {peak}");
        foreach (var name in TableClients.KnownTables) Assert.True(tables.GetAsync(name, CancellationToken.None).IsCompletedSuccessfully, name);
    }

    [Fact]
    public async Task A_failed_creation_is_retried_by_the_next_caller()
    {
        var attempts = 0;
        var tables = new TableClients(name => ++attempts == 1 ? Task.FromException<TableClient>(new InvalidOperationException("storage down")) : Task.FromResult(Client(name)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tables.GetAsync("settings", CancellationToken.None));
        var client = await tables.GetAsync("settings", CancellationToken.None);
        Assert.Equal("settings", client.Name);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_caller_that_gives_up_waiting_does_not_discard_the_creation_in_progress()
    {
        var created = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tables = new TableClients(async name => { Interlocked.Increment(ref created); await gate.Task; return Client(name); });
        using var cts = new CancellationTokenSource();
        var impatient = tables.GetAsync("views", cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        gate.SetResult();
        var client = await tables.GetAsync("views", CancellationToken.None);
        Assert.Equal("views", client.Name);
        Assert.Equal(1, created);
    }

    [Fact]
    public void Every_table_store_uses_a_known_table()
    {
        // Every string constant whose name ends in "Table" inside a store (Table, ActiveTable, DeckViewsTable, ...)
        // names a table the warm-up and the backup must know about.
        var constants = typeof(TableClients).Assembly.GetTypes()
            .Where(t => t.Namespace == "Podium.Web.Storage")
            .SelectMany(t => t.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Table", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Distinct()
            .ToList();
        Assert.Equal(TableClients.KnownTables.Length, constants.Count);
        foreach (var name in constants) Assert.Contains(name, TableClients.KnownTables);
        Assert.Equal(TableClients.KnownTables, BackupCommand.Tables);
    }
}

/// <summary>What the host does right after it starts listening.</summary>
[Collection(nameof(WebCollection))]
public sealed class StartupTests(PodiumWebFactory app)
{
    [Fact]
    public async Task The_key_ring_is_loaded_by_the_warm_up_rather_than_before_the_server_listens()
    {
        Assert.DoesNotContain(app.Services.GetServices<IStartupFilter>(), f => f.GetType().FullName == "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionStartupFilter");
        var result = await app.Services.GetRequiredService<StartupWarmup>().Completed.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.KeyRingLoaded);
        Assert.False(result.TablesReady); // memory storage has no tables to create
        Assert.Equal(0, result.TableCount);
    }

    [Fact]
    public async Task The_first_served_request_releases_the_maintenance_work_but_probes_do_not()
    {
        // The signal is process-wide; another test's request may already have tripped it, so only the probe rule is
        // asserted through a fresh signal instance and the middleware's own rule.
        var first = new FirstRequest();
        Assert.False(first.HasBeenSeen);
        first.Mark(); first.Mark();
        Assert.True(first.HasBeenSeen);
        await first.Seen;
        var live = app.Services.GetRequiredService<FirstRequest>();
        var before = live.HasBeenSeen;
        Assert.Equal(System.Net.HttpStatusCode.OK, (await app.Client().GetAsync("/healthz")).StatusCode);
        if (!before) Assert.False(live.HasBeenSeen, "a health probe must not count as the first request");
        await app.Client().SendAsync(PodiumWebFactory.Navigation("/login"));
        Assert.True(live.HasBeenSeen);
    }
}
