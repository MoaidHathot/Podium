using System.Collections.Concurrent;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Core.InMemory;

/// <summary>Volatile stores used by tests and by the local development profile.</summary>
public sealed class InMemorySourceStore : ISourceStore
{
    private readonly ConcurrentDictionary<string, Source> _items = new(StringComparer.Ordinal);
    public Task<Source?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault(id));
    public Task<IReadOnlyList<Source>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Source>>(_items.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList());
    public Task UpsertAsync(Source source, CancellationToken ct = default) { _items[source.Id] = source; return Task.CompletedTask; }
    public Task DeleteAsync(string id, CancellationToken ct = default) { _items.TryRemove(id, out _); return Task.CompletedTask; }
}

public sealed class InMemoryDeckStore : IDeckStore
{
    private readonly ConcurrentDictionary<string, Deck> _items = new(StringComparer.Ordinal);
    public Task<Deck?> GetAsync(string slug, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault(slug));
    public Task<Deck?> GetByAliasAsync(string alias, CancellationToken ct = default) => Task.FromResult(_items.Values.FirstOrDefault(d => string.Equals(d.Alias, alias, StringComparison.Ordinal)));
    public Task<IReadOnlyList<Deck>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Deck>>(_items.Values.Where(d => includeArchived || !d.Archived).OrderBy(d => d.Slug, StringComparer.Ordinal).ToList());
    public Task<IReadOnlyList<Deck>> ListBySourceAsync(string sourceId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Deck>>(_items.Values.Where(d => d.SourceId == sourceId).OrderBy(d => d.Slug, StringComparer.Ordinal).ToList());
    public Task UpsertAsync(Deck deck, CancellationToken ct = default) { _items[deck.Slug] = deck; return Task.CompletedTask; }
    public Task DeleteAsync(string slug, CancellationToken ct = default) { _items.TryRemove(slug, out _); return Task.CompletedTask; }
}

public sealed class InMemoryBuildStore : IBuildStore
{
    private readonly ConcurrentDictionary<(string, string), Build> _items = new();
    public Task<Build?> GetAsync(string deckSlug, string buildId, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault((deckSlug, buildId)));
    public Task<IReadOnlyList<Build>> ListForDeckAsync(string deckSlug, int take = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Build>>(_items.Values.Where(b => b.DeckSlug == deckSlug).OrderByDescending(b => b.Id, StringComparer.Ordinal).Take(take).ToList());
    public Task<IReadOnlyList<Build>> ListActiveAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Build>>(_items.Values.Where(b => b.Status is BuildStatus.Queued or BuildStatus.Running).ToList());
    public Task UpsertAsync(Build build, CancellationToken ct = default) { _items[(build.DeckSlug, build.Id)] = build; return Task.CompletedTask; }
    public Task DeleteAsync(string deckSlug, string buildId, CancellationToken ct = default) { _items.TryRemove((deckSlug, buildId), out _); return Task.CompletedTask; }
}

public sealed class InMemoryGrantStore : IGrantStore
{
    private readonly ConcurrentDictionary<(string, string), Grant> _items = new();
    public Task<IReadOnlyList<Grant>> ListForDeckAsync(string deckSlug, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Grant>>(_items.Values.Where(g => g.DeckSlug == deckSlug).OrderBy(g => g.Principal, StringComparer.Ordinal).ToList());
    public Task UpsertAsync(Grant grant, CancellationToken ct = default) { _items[(grant.DeckSlug, grant.Principal)] = grant; return Task.CompletedTask; }
    public Task DeleteAsync(string deckSlug, string principal, CancellationToken ct = default) { _items.TryRemove((deckSlug, principal), out _); return Task.CompletedTask; }
}

public sealed class InMemoryShareLinkStore : IShareLinkStore
{
    private readonly ConcurrentDictionary<string, ShareLink> _items = new(StringComparer.Ordinal);
    public Task<ShareLink?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault(id));
    public Task<IReadOnlyList<ShareLink>> ListForDeckAsync(string deckSlug, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShareLink>>(_items.Values.Where(l => l.DeckSlug == deckSlug).OrderByDescending(l => l.CreatedAt).ToList());
    public Task UpsertAsync(ShareLink link, CancellationToken ct = default) { _items[link.Id] = link; return Task.CompletedTask; }
}

public sealed class InMemoryViewHistoryStore : IViewHistoryStore
{
    private readonly ConcurrentQueue<ViewEvent> _items = new();
    private readonly ConcurrentDictionary<(string Principal, string Slug), DateTimeOffset> _dismissals = new();

    public Task DismissRecentAsync(string principal, string deckSlug, DateTimeOffset at, CancellationToken ct = default)
    {
        _dismissals[(principal, deckSlug)] = at;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetRecentDismissalsAsync(string principal, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(_dismissals.Where(kv => kv.Key.Principal == principal).ToDictionary(kv => kv.Key.Slug, kv => kv.Value, StringComparer.Ordinal));
    public Task RecordAsync(ViewEvent e, CancellationToken ct = default)
    {
        _items.Enqueue(e);
        while (_items.Count > 5000 && _items.TryDequeue(out _)) { }
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<ViewEvent>> RecentForPrincipalAsync(string principal, int take = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ViewEvent>>(_items.Where(v => v.Principal == principal).OrderByDescending(v => v.At).Take(take).ToList());
    public Task<IReadOnlyList<ViewEvent>> RecentForDeckAsync(string deckSlug, int take = 100, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ViewEvent>>(_items.Where(v => v.DeckSlug == deckSlug).OrderByDescending(v => v.At).Take(take).ToList());
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<(string, string), Session> _items = new();
    public Task<Session?> GetAsync(string deckSlug, string id, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault((deckSlug, id)));
    public Task<IReadOnlyList<Session>> ListForDeckAsync(string deckSlug, int take = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Session>>(_items.Values.Where(s => s.DeckSlug == deckSlug).OrderByDescending(s => s.StartedAt).Take(take).ToList());
    public Task<IReadOnlyList<Session>> ListLiveAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Session>>(_items.Values.Where(s => s.EndedAt is null).OrderBy(s => s.StartedAt).ToList());
    public Task UpsertAsync(Session session, CancellationToken ct = default) { _items[(session.DeckSlug, session.Id)] = session; return Task.CompletedTask; }
}

public sealed class InMemoryAccessRequestStore : IAccessRequestStore
{
    private readonly ConcurrentDictionary<(string, string), AccessRequest> _items = new();
    public Task<AccessRequest?> GetAsync(string deckSlug, string principal, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault((deckSlug, principal)));
    public Task<IReadOnlyList<AccessRequest>> ListForDeckAsync(string deckSlug, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccessRequest>>(_items.Values.Where(r => r.DeckSlug == deckSlug).OrderByDescending(r => r.RequestedAt).ToList());
    public Task<IReadOnlyList<AccessRequest>> ListPendingAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccessRequest>>(_items.Values.Where(r => r.Status == AccessRequestStatus.Pending).OrderByDescending(r => r.RequestedAt).ToList());
    public Task UpsertAsync(AccessRequest request, CancellationToken ct = default) { _items[(request.DeckSlug, request.Principal)] = request; return Task.CompletedTask; }
    public Task DeleteAsync(string deckSlug, string principal, CancellationToken ct = default) { _items.TryRemove((deckSlug, principal), out _); return Task.CompletedTask; }
}

public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly ConcurrentQueue<AuditEntry> _items = new();
    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        _items.Enqueue(entry);
        while (_items.Count > 10000 && _items.TryDequeue(out _)) { }
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<AuditEntry>> RecentAsync(string? target = null, int take = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuditEntry>>(_items.Where(e => target is null || e.Target == target).OrderByDescending(e => e.At).Take(take).ToList());
}

public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly ConcurrentDictionary<string, string> _items = new(StringComparer.Ordinal);
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault(key));
    public Task SetAsync(string key, string value, CancellationToken ct = default) { _items[key] = value; return Task.CompletedTask; }
}

public sealed class InMemoryDeviceStore : IDeviceStore
{
    private readonly ConcurrentDictionary<(string, string), Device> _items = new();
    public Task<Device?> GetAsync(string principal, string sid, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault((principal, sid)));
    public Task<IReadOnlyList<Device>> ListAsync(string principal, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Device>>(_items.Values.Where(d => d.Principal == principal).OrderByDescending(d => d.LastSeenAt).ToList());
    public Task UpsertAsync(Device device, CancellationToken ct = default) { _items[(device.Principal, device.Sid)] = device; return Task.CompletedTask; }
    public Task DeleteAsync(string principal, string sid, CancellationToken ct = default) { _items.TryRemove((principal, sid), out _); return Task.CompletedTask; }
}
