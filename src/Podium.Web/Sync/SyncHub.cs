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

    public async Task HandleAsync(HttpContext http, string slug, Caller caller, bool canPresent, CancellationToken ct)
    {
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var room = _rooms.GetOrAdd(slug, _ => new Room());
        if (room.Sockets.Count >= MaxSocketsPerRoom)
        {
            await socket.CloseAsync((WebSocketCloseStatus)4429, "room full", ct);
            return;
        }

        var id = Guid.NewGuid();
        var canSend = canPresent;
        var entry = (socket, canSend, new SemaphoreSlim(1, 1));
        room.Sockets[id] = entry;
        log.LogDebug("Sync join {Slug} by {Principal} (send={CanSend}); {Count} in room", slug, caller.Principal ?? "anonymous", canSend, room.Sockets.Count);

        try
        {
            // Tell the client what it may do, then replay the latest known state so this window catches up immediately.
            await SendAsync(entry, JsonSerializer.Serialize(new { t = "hello", canSend }), ct);
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
                    var (kind, channel) = Validate(payload);
                    switch (kind)
                    {
                        case "state":
                            room.LastState[channel!] = payload;
                            await BroadcastAsync(room, id, payload, ct);
                            break;
                        case "info":
                            // Current position reported by a presenting instance; kept so a remote joining later knows where we are.
                            room.LastState["\u0000info"] = payload;
                            await BroadcastAsync(room, id, payload, ct);
                            break;
                        case "nav":
                            await BroadcastAsync(room, id, payload, ct);
                            break;
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

    /// <summary>Tells every open instance of a deck that a new build is being served (they decide how to react).</summary>
    public async Task NotifyBuildAsync(string slug, string buildId, CancellationToken ct = default)
    {
        if (!_rooms.TryGetValue(slug, out var room)) return;
        var payload = JsonSerializer.Serialize(new { t = "build", build = buildId });
        await BroadcastAsync(room, Guid.Empty, payload, ct);
        log.LogDebug("Notified {Count} sockets of build {Build} for {Slug}", room.Sockets.Count, buildId, slug);
    }

    private static readonly HashSet<string> NavActions = new(StringComparer.Ordinal) { "next", "prev", "first", "last", "go", "nextSlide", "prevSlide" };

    /// <summary>
    /// Accepts exactly three message shapes from presenting sockets:
    ///   {"t":"state","channel":string,"state":object}        Slidev shared/drawing state
    ///   {"t":"nav","action":string,"page"?:int}             remote-control command
    ///   {"t":"info","page":int,"total":int,"clicks":int,"clicksTotal":int}  position report
    /// Returns (kind, channel) or (null, null) for anything else.
    /// </summary>
    private static (string? Kind, string? Channel) Validate(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return (null, null);
            switch (t.GetString())
            {
                case "state":
                    if (!root.TryGetProperty("channel", out var c) || c.ValueKind != JsonValueKind.String) return (null, null);
                    if (!root.TryGetProperty("state", out var s) || s.ValueKind != JsonValueKind.Object) return (null, null);
                    var channel = c.GetString();
                    return string.IsNullOrEmpty(channel) || channel.Length > 200 ? (null, null) : ("state", channel);
                case "nav":
                    if (!root.TryGetProperty("action", out var a) || a.ValueKind != JsonValueKind.String || !NavActions.Contains(a.GetString()!)) return (null, null);
                    if (root.TryGetProperty("page", out var p) && (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var pn) || pn < 1 || pn > 10_000)) return (null, null);
                    return ("nav", null);
                case "info":
                    foreach (var f in new[] { "page", "total", "clicks", "clicksTotal" })
                        if (!root.TryGetProperty(f, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < 0 || n > 100_000) return (null, null);
                    return ("info", null);
                default:
                    return (null, null);
            }
        }
        catch (JsonException) { return (null, null); }
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

            await hub.HandleAsync(http, slug, caller, result.CanPresent, http.RequestAborted);
            return Results.Empty;
        });
        return app;
    }
}
