using System.Security.Cryptography;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Web.Security;

/// <summary>Writes the audit trail: who changed what, when, from where. Best effort - never fails the action.</summary>
public sealed class AuditService(IAuditStore store, CallerResolver callers, ILogger<AuditService> log)
{
    public async Task RecordAsync(HttpContext http, string action, string? target, string? details = null)
    {
        try
        {
            var caller = callers.Resolve(http.User);
            await store.AppendAsync(new AuditEntry
            {
                Id = NewId(),
                Actor = caller.Principal ?? "anonymous",
                Action = action,
                Target = target,
                Details = details is { Length: > 500 } ? details[..500] : details,
                Ip = http.Connection.RemoteIpAddress?.ToString(),
            }, http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Audit entry {Action} on {Target} could not be written", action, target);
        }
    }

    /// <summary>Entries written by background work (no HTTP context).</summary>
    public async Task RecordSystemAsync(string action, string? target, string? details = null, CancellationToken ct = default)
    {
        try { await store.AppendAsync(new AuditEntry { Id = NewId(), Actor = "system", Action = action, Target = target, Details = details }, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Audit entry {Action} on {Target} could not be written", action, target); }
    }

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[9];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
    }
}
