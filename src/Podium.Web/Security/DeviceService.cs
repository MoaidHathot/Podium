using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Web.Security;

/// <summary>
/// Signed-in devices. Every login gets a random session id (<c>urn:podium:sid</c>) and a <see cref="Device"/> record
/// (browser summary, address, created / last seen). Each request checks the record is not revoked (30 s cache) and
/// refreshes "last seen" at most every ten minutes. Revoking one device signs out that browser only.
/// </summary>
public sealed class DeviceService(IDeviceStore devices, IMemoryCache cache, ILogger<DeviceService> log)
{
    public const string SidClaim = "urn:podium:sid";
    private static readonly TimeSpan RevocationCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(10);

    public static string NewSid()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Called when a cookie is issued: records the device and returns the sid to embed in the ticket.</summary>
    public async Task<string> RegisterAsync(string principal, HttpContext http, CancellationToken ct = default)
    {
        var sid = NewSid();
        await devices.UpsertAsync(new Device
        {
            Principal = principal,
            Sid = sid,
            Client = Summarize(http.Request.Headers.UserAgent.ToString()),
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        }, ct);
        return sid;
    }

    /// <summary>True when the device behind a ticket was revoked (or deleted). Tickets without a sid are not tracked.</summary>
    public async Task<bool> IsRevokedAsync(string principal, string sid, CancellationToken ct = default)
    {
        var key = $"device:{principal}:{sid}";
        if (cache.TryGetValue(key, out bool revoked)) return revoked;
        var d = await devices.GetAsync(principal, sid, ct);
        revoked = d is null || d.Revoked;
        cache.Set(key, revoked, RevocationCacheTtl);
        return revoked;
    }

    /// <summary>Refreshes last-seen (and the current address) at most every ten minutes per device.</summary>
    public async Task TouchAsync(string principal, string sid, HttpContext http, CancellationToken ct = default)
    {
        var key = $"device-touch:{principal}:{sid}";
        if (cache.TryGetValue(key, out _)) return;
        cache.Set(key, true, TouchInterval);
        try
        {
            var d = await devices.GetAsync(principal, sid, ct);
            if (d is null || d.Revoked) return;
            await devices.UpsertAsync(d with { LastSeenAt = DateTimeOffset.UtcNow, Ip = http.Connection.RemoteIpAddress?.ToString() ?? d.Ip }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Device touch failed"); }
    }

    public async Task<bool> RevokeAsync(string principal, string sid, CancellationToken ct = default)
    {
        var d = await devices.GetAsync(principal, sid, ct);
        if (d is null) return false;
        if (!d.Revoked) await devices.UpsertAsync(d with { Revoked = true, RevokedAt = DateTimeOffset.UtcNow }, ct);
        cache.Set($"device:{principal}:{sid}", true, RevocationCacheTtl);
        return true;
    }

    public Task<IReadOnlyList<Device>> ListAsync(string principal, CancellationToken ct = default) => devices.ListAsync(principal, ct);

    /// <summary>"Chrome on Windows", "Safari on iPhone", ... from the user agent, never the raw string.</summary>
    internal static string Summarize(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return "Unknown browser";
        string browser =
            ua.Contains("Edg/", StringComparison.Ordinal) ? "Edge" :
            ua.Contains("OPR/", StringComparison.Ordinal) ? "Opera" :
            ua.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox" :
            ua.Contains("CriOS/", StringComparison.Ordinal) || ua.Contains("Chrome/", StringComparison.Ordinal) ? "Chrome" :
            ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari" : "Browser";
        string os =
            ua.Contains("iPhone", StringComparison.Ordinal) ? "iPhone" :
            ua.Contains("iPad", StringComparison.Ordinal) ? "iPad" :
            ua.Contains("Android", StringComparison.Ordinal) ? "Android" :
            ua.Contains("Windows", StringComparison.Ordinal) ? "Windows" :
            ua.Contains("Mac OS X", StringComparison.Ordinal) ? "macOS" :
            ua.Contains("CrOS", StringComparison.Ordinal) ? "ChromeOS" :
            ua.Contains("Linux", StringComparison.Ordinal) ? "Linux" : "unknown OS";
        return $"{browser} on {os}";
    }

    public static string? SidOf(ClaimsPrincipal? user) => user?.FindFirstValue(SidClaim);
}
