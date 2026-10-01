using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Podium.Core.Models;
using Podium.Core.Security;
using Podium.Web.Security;

namespace Podium.Web.Sync;

/// <summary>
/// Relays Slidev shared state between all open instances of a deck. One room per deck. Only sockets belonging to the
/// owner may publish; everyone the access policy admits may listen. The last state per channel is replayed to
/// newcomers so a freshly opened audience window jumps to the presenter's current slide.
/// </summary>
public sealed class SyncHub(ILogger<SyncHub> log)
{
    private const int MaxMessageBytes = 256 * 1024;
    private const int MaxSocketsPerRoom = 50;
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);

    private sealed class Room
    {
        public readonly ConcurrentDictionary<Guid, (WebSocket Socket, bool CanSend, SemaphoreSlim Lock)> Sockets = new();
        public readonly ConcurrentDictionary<string, string> LastState = new(StringComparer.Ordinal);
    }

    public async Task HandleAsync(HttpContext http, string slug, Caller caller, CancellationToken ct)
    {
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var room = _rooms.GetOrAdd(slug, _ => new Room());
        if (room.Sockets.Count >= MaxSocketsPerRoom)
        {
            await socket.CloseAsync((WebSocketCloseStatus)4429, "room full", ct);
            return;
        }

        var id = Guid.NewGuid();
        var canSend = caller.IsOwner;
        var entry = (socket, canSend, new SemaphoreSlim(1, 1));
        room.Sockets[id] = entry;
        log.LogDebug("Sync join {Slug} by {Principal} (send={CanSend}); {Count} in room", slug, caller.Principal ?? "anonymous", canSend, room.Sockets.Count);

        try
        {
            // Replay the latest known state so this window catches up immediately.
            foreach (var (_, payload) in room.LastState)
                await SendAsync(entry, payload, ct);

            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    await socket.CloseAsync((WebSocketCloseStatus)4413, "message too large", ct);
                    break;
                }
                if (!result.EndOfMessage) continue;

                if (result.MessageType == WebSocketMessageType.Text && canSend)
                {
                    var payload = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    var channel = ValidateAndGetChannel(payload);
                    if (channel is not null)
                    {
                        room.LastState[channel] = payload;
                        await BroadcastAsync(room, id, payload, ct);
                    }
                }
                message.SetLength(0);
            }
        }
        catch (WebSocketException) { /* client went away */ }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            room.Sockets.TryRemove(id, out _);
            entry.Item3.Dispose();
            if (room.Sockets.IsEmpty) _rooms.TryRemove(slug, out _);
            log.LogDebug("Sync leave {Slug}; {Count} left", slug, room.Sockets.Count);
        }
    }

    /// <summary>Accepts only {"t":"state","channel":string,"state":object}. Returns the channel or null.</summary>
    private static string? ValidateAndGetChannel(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("t", out var t) || t.GetString() != "state") return null;
            if (!root.TryGetProperty("channel", out var c) || c.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("state", out var s) || s.ValueKind != JsonValueKind.Object) return null;
            var channel = c.GetString();
            return string.IsNullOrEmpty(channel) || channel.Length > 200 ? null : channel;
        }
        catch (JsonException) { return null; }
    }

    private async Task BroadcastAsync(Room room, Guid sender, string payload, CancellationToken ct)
    {
        foreach (var (id, entry) in room.Sockets)
        {
            if (id == sender) continue;
            try { await SendAsync(entry, payload, ct); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                room.Sockets.TryRemove(id, out _);
            }
        }
    }

    private static async Task SendAsync((WebSocket Socket, bool CanSend, SemaphoreSlim Lock) entry, string payload, CancellationToken ct)
    {
        if (entry.Socket.State != WebSocketState.Open) return;
        await entry.Lock.WaitAsync(ct);
        try { await entry.Socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct); }
        finally { entry.Lock.Release(); }
    }
}

public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSync(this IEndpointRouteBuilder app)
    {
        app.Map("/ws/sync/{slug}", async (string slug, HttpContext http, Serving.DeckAccessService access, CallerResolver callers, SyncHub hub, CancellationToken ct) =>
        {
            if (!http.WebSockets.IsWebSocketRequest) return Results.BadRequest("WebSocket expected");
            // Same-origin only: browsers always send Origin on WebSocket upgrades.
            var origin = http.Request.Headers.Origin.ToString();
            var expected = $"{http.Request.Scheme}://{http.Request.Host}";
            if (!string.IsNullOrEmpty(origin) && !string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var caller = callers.Resolve(http.User);
            var result = await access.EvaluateAsync(http, slug, ArtifactKind.Site, caller, ct);
            if (result.Deck is null || result.Decision != AccessDecision.Allow) return Results.StatusCode(StatusCodes.Status403Forbidden);

            await hub.HandleAsync(http, slug, caller, http.RequestAborted);
            return Results.Empty;
        });
        return app;
    }
}
