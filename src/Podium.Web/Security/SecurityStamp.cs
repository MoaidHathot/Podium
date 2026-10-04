using Microsoft.Extensions.Caching.Memory;
using Podium.Core.Abstractions;

namespace Podium.Web.Security;

/// <summary>
/// "Sign out everywhere": the owner can invalidate every session cookie at once without rotating keys. The stamp is
/// the UTC time of the last invalidation; a cookie ticket issued before it is rejected on its next request. Cached
/// briefly so the check costs nothing per request.
/// </summary>
public sealed class SecurityStamp(ISettingsStore settings, IMemoryCache cache)
{
    private const string Key = "security:stamp";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public async Task<DateTimeOffset?> GetAsync(CancellationToken ct = default)
        => await cache.GetOrCreateAsync(Key, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheTtl;
            var raw = await settings.GetAsync(Key, ct);
            return raw is not null && DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : (DateTimeOffset?)null;
        });

    /// <summary>Invalidates every ticket issued up to now.</summary>
    public async Task BumpAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await settings.SetAsync(Key, now.ToString("o"), ct);
        cache.Set(Key, (DateTimeOffset?)now, CacheTtl);
    }

    public async Task<bool> IsTicketValidAsync(DateTimeOffset? issuedUtc, CancellationToken ct = default)
    {
        var stamp = await GetAsync(ct);
        if (stamp is null) return true;
        // Tickets without an issue time cannot prove they post-date the stamp. Ticket times are stored with second
        // precision (RFC 1123), so the stamp is compared at the same granularity.
        var floor = new DateTimeOffset(stamp.Value.UtcTicks - stamp.Value.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        return issuedUtc is { } issued && issued >= floor;
    }
}
