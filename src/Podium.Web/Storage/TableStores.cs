using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Data.Tables;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Web.Storage;

/// <summary>
/// Shared helpers for the Table Storage stores. Every entity is stored as JSON in a single "Json" column plus a few
/// typed columns used for filtering. This keeps the schema evolvable without migrations.
/// </summary>
internal static class TableJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(TableEntity e) => e.TryGetValue("Json", out var v) && v is string s ? JsonSerializer.Deserialize<T>(s, Options) : default;

    /// <summary>Table keys may not contain / \ # ? or control chars.</summary>
    public static string Key(string s) => s.Replace('/', '|').Replace('\\', '|').Replace('#', '_').Replace('?', '_');

    public static string InvertedTicks(DateTimeOffset at) => (DateTimeOffset.MaxValue.UtcTicks - at.UtcTicks).ToString("D19", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class TableClients
{
    private readonly TableServiceClient _service;
    private readonly string _prefix;
    private readonly Dictionary<string, TableClient> _clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TableClients(TableServiceClient service, string prefix)
    {
        _service = service;
        _prefix = prefix;
    }

    public async ValueTask<TableClient> GetAsync(string name, CancellationToken ct)
    {
        if (_clients.TryGetValue(name, out var c)) return c;
        await _lock.WaitAsync(ct);
        try
        {
            if (_clients.TryGetValue(name, out c)) return c;
            c = _service.GetTableClient(_prefix + name);
            await c.CreateIfNotExistsAsync(ct);
            _clients[name] = c;
            return c;
        }
        finally { _lock.Release(); }
    }
}

public sealed class TableSourceStore(TableClients tables) : ISourceStore
{
    private const string Table = "sources";

    public async Task<Source?> GetAsync(string id, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        try
        {
            var e = await t.GetEntityAsync<TableEntity>("source", TableJson.Key(id), cancellationToken: ct);
            return TableJson.Deserialize<Source>(e.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<IReadOnlyList<Source>> ListAsync(CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        var list = new List<Source>();
        await foreach (var e in t.QueryAsync<TableEntity>(x => x.PartitionKey == "source", cancellationToken: ct))
        {
            var s = TableJson.Deserialize<Source>(e);
            if (s is not null) list.Add(s);
        }
        return list.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    public async Task UpsertAsync(Source source, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        var e = new TableEntity("source", TableJson.Key(source.Id)) { ["Json"] = TableJson.Serialize(source), ["Trusted"] = source.Trusted };
        await t.UpsertEntityAsync(e, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        await t.DeleteEntityAsync("source", TableJson.Key(id), cancellationToken: ct);
    }
}

public sealed class TableDeckStore(TableClients tables) : IDeckStore
{
    private const string Table = "decks";

    public async Task<Deck?> GetAsync(string slug, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        try
        {
            var e = await t.GetEntityAsync<TableEntity>("deck", slug, cancellationToken: ct);
            return TableJson.Deserialize<Deck>(e.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<Deck?> GetByAliasAsync(string alias, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        await foreach (var e in t.QueryAsync<TableEntity>(x => x.PartitionKey == "deck" && x.GetString("Alias") == alias, maxPerPage: 1, cancellationToken: ct))
            return TableJson.Deserialize<Deck>(e);
        return null;
    }

    public async Task<IReadOnlyList<Deck>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        var list = new List<Deck>();
        await foreach (var e in t.QueryAsync<TableEntity>(x => x.PartitionKey == "deck", cancellationToken: ct))
        {
            var d = TableJson.Deserialize<Deck>(e);
            if (d is not null && (includeArchived || !d.Archived)) list.Add(d);
        }
        return list.OrderBy(d => d.Slug, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<Deck>> ListBySourceAsync(string sourceId, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        var list = new List<Deck>();
        await foreach (var e in t.QueryAsync<TableEntity>(x => x.PartitionKey == "deck" && x.GetString("SourceId") == sourceId, cancellationToken: ct))
        {
            var d = TableJson.Deserialize<Deck>(e);
            if (d is not null) list.Add(d);
        }
        return list.OrderBy(d => d.Slug, StringComparer.Ordinal).ToList();
    }

    public async Task UpsertAsync(Deck deck, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        var e = new TableEntity("deck", deck.Slug)
        {
            ["Json"] = TableJson.Serialize(deck),
            ["SourceId"] = deck.SourceId,
            ["Archived"] = deck.Archived,
            ["Visibility"] = deck.Visibility.ToString(),
            ["Alias"] = deck.Alias ?? "",
        };
        await t.UpsertEntityAsync(e, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string slug, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        try { await t.DeleteEntityAsync("deck", slug, cancellationToken: ct); }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}

public sealed class TableBuildStore(TableClients tables) : IBuildStore
{
    private const string Table = "builds";

    // RowKey = inverted build id ordering is not possible lexicographically for ascending ids, so we store the id and sort in memory.
    public async Task<Build?> GetAsync(string deckSlug, string buildId, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        try
        {
            var e = await t.GetEntityAsync<TableEntity>(deckSlug, buildId, cancellationToken: ct);
            return TableJson.Deserialize<Build>(e.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<IReadOnlyList<Build>> ListForDeckAsync(string deckSlug, int take = 20, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        var list = new List<Build>();
        await foreach (var e in t.QueryAsync<TableEntity>(x => x.PartitionKey == deckSlug, cancellationToken: ct))
        {
            var b = TableJson.Deserialize<Build>(e);
            if (b is not null) list.Add(b);
        }
        return list.OrderByDescending(b => b.Id, StringComparer.Ordinal).Take(take).ToList();
    }

    // Active builds are mirrored into a small "activebuilds" table keyed by (deck, build), so the once-a-minute reaper
    // and every queue operation read a handful of rows instead of scanning the whole build history.
    private const string ActiveTable = "activebuilds";

    public async Task<IReadOnlyList<Build>> ListActiveAsync(CancellationToken ct = default)
    {
        var active = await tables.GetAsync(ActiveTable, ct);
        var list = new List<Build>();
        var stale = new List<TableEntity>();
        await foreach (var e in active.QueryAsync<TableEntity>(cancellationToken: ct))
        {
            var b = TableJson.Deserialize<Build>(e);
            if (b is null) { stale.Add(e); continue; }
            if (b.Status is BuildStatus.Queued or BuildStatus.Running) list.Add(b); else stale.Add(e);
        }
        foreach (var e in stale) { try { await active.DeleteEntityAsync(e.PartitionKey, e.RowKey, cancellationToken: ct); } catch (RequestFailedException) { } }
        return list;
    }

    public async Task UpsertAsync(Build build, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        var isActive = build.Status is BuildStatus.Queued or BuildStatus.Running;
        var e = new TableEntity(build.DeckSlug, build.Id)
        {
            ["Json"] = TableJson.Serialize(build),
            ["Active"] = isActive,
            ["Status"] = build.Status.ToString(),
        };
        await t.UpsertEntityAsync(e, TableUpdateMode.Replace, ct);

        var active = await tables.GetAsync(ActiveTable, ct);
        if (isActive)
            await active.UpsertEntityAsync(new TableEntity(build.DeckSlug, build.Id) { ["Json"] = TableJson.Serialize(build) }, TableUpdateMode.Replace, ct);
        else
        {
            try { await active.DeleteEntityAsync(build.DeckSlug, build.Id, cancellationToken: ct); }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }
    }

    public async Task DeleteAsync(string deckSlug, string buildId, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        try { await t.DeleteEntityAsync(deckSlug, buildId, cancellationToken: ct); }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}

public sealed class TableGrantStore(TableClients tables) : IGrantStore
{
    private const string Table = "grants";

    public async Task<IReadOnlyList<Grant>> ListForDeckAsync(string deckSlug, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        var list = new List<Grant>();
        await foreach (var e in t.QueryAsync<TableEntity>(x => x.PartitionKey == deckSlug, cancellationToken: ct))
        {
            var g = TableJson.Deserialize<Grant>(e);
            if (g is not null) list.Add(g);
        }
        return list.OrderBy(g => g.Principal, StringComparer.Ordinal).ToList();
    }

    public async Task UpsertAsync(Grant grant, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        await t.UpsertEntityAsync(new TableEntity(grant.DeckSlug, TableJson.Key(grant.Principal)) { ["Json"] = TableJson.Serialize(grant) }, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string deckSlug, string principal, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        await t.DeleteEntityAsync(deckSlug, TableJson.Key(principal), cancellationToken: ct);
    }
}

public sealed class TableShareLinkStore(TableClients tables) : IShareLinkStore
{
    private const string Table = "sharelinks";

    public async Task<ShareLink?> GetAsync(string id, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        try
        {
            var e = await t.GetEntityAsync<TableEntity>("link", id, cancellationToken: ct);
            return TableJson.Deserialize<ShareLink>(e.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<IReadOnlyList<ShareLink>> ListForDeckAsync(string deckSlug, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        var list = new List<ShareLink>();
        await foreach (var e in t.QueryAsync<TableEntity>(x => x.PartitionKey == "link" && x.GetString("DeckSlug") == deckSlug, cancellationToken: ct))
        {
            var l = TableJson.Deserialize<ShareLink>(e);
            if (l is not null) list.Add(l);
        }
        return list.OrderByDescending(l => l.CreatedAt).ToList();
    }

    public async Task UpsertAsync(ShareLink link, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        await t.UpsertEntityAsync(new TableEntity("link", link.Id) { ["Json"] = TableJson.Serialize(link), ["DeckSlug"] = link.DeckSlug }, TableUpdateMode.Replace, ct);
    }
}

public sealed class TableViewHistoryStore(TableClients tables) : IViewHistoryStore
{
    private const string Table = "views";

    private const string DeckViewsTable = "deckviews";

    public async Task RecordAsync(ViewEvent e, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        var entity = new TableEntity(TableJson.Key(e.Principal), TableJson.InvertedTicks(e.At) + "_" + e.DeckSlug) { ["Json"] = TableJson.Serialize(e) };
        await t.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        // Second copy partitioned by deck: the per-deck analytics query becomes a cheap partition scan.
        var byDeck = await tables.GetAsync(DeckViewsTable, ct);
        await byDeck.UpsertEntityAsync(new TableEntity(e.DeckSlug, TableJson.InvertedTicks(e.At) + "_" + TableJson.Key(e.Principal)) { ["Json"] = TableJson.Serialize(e) }, TableUpdateMode.Replace, ct);
    }

    public async Task<IReadOnlyList<ViewEvent>> RecentForDeckAsync(string deckSlug, int take = 100, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(DeckViewsTable, ct);
        var list = new List<ViewEvent>();
        await foreach (var page in t.QueryAsync<TableEntity>(x => x.PartitionKey == deckSlug, maxPerPage: take, cancellationToken: ct).AsPages())
        {
            foreach (var e in page.Values)
            {
                var v = TableJson.Deserialize<ViewEvent>(e);
                if (v is not null) list.Add(v);
            }
            if (list.Count >= take) break;
        }
        return list.OrderByDescending(v => v.At).Take(take).ToList();
    }

    public async Task<IReadOnlyList<ViewEvent>> RecentForPrincipalAsync(string principal, int take = 20, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(Table, ct);
        var list = new List<ViewEvent>();
        var pk = TableJson.Key(principal);
        await foreach (var page in t.QueryAsync<TableEntity>(x => x.PartitionKey == pk, maxPerPage: take * 5, cancellationToken: ct).AsPages())
        {
            foreach (var e in page.Values)
            {
                var v = TableJson.Deserialize<ViewEvent>(e);
                if (v is not null) list.Add(v);
            }
            if (list.Count >= take * 5) break;
        }
        return list.OrderByDescending(v => v.At).Take(take).ToList();
    }

    private const string DismissTable = "recentdismissals";

    public async Task DismissRecentAsync(string principal, string deckSlug, DateTimeOffset at, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(DismissTable, ct);
        await t.UpsertEntityAsync(new TableEntity(TableJson.Key(principal), deckSlug) { ["At"] = at }, TableUpdateMode.Replace, ct);
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetRecentDismissalsAsync(string principal, CancellationToken ct = default)
    {
        var t = await tables.GetAsync(DismissTable, ct);
        var pk = TableJson.Key(principal);
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        await foreach (var e in t.QueryAsync<TableEntity>(x => x.PartitionKey == pk, cancellationToken: ct))
        {
            if (e.GetDateTimeOffset("At") is { } at) result[e.RowKey] = at;
        }
        return result;
    }
}
