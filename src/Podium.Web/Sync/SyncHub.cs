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
/// owner (or Present grantees) may publish; everyone the access policy admits may listen. The last state per channel
/// is replayed to newcomers so a freshly opened audience window jumps to the presenter's current slide.
///
/// Besides the raw relay the hub keeps light room metadata: how many presenters and viewers are connected
/// (<c>presence</c>, sent to everyone), and a presenter-raised <c>screen</c> (blackout / message) that newcomers also
/// receive. Cached presenter state is dropped when the last presenter leaves, so a viewer opening the deck the next
/// day is not teleported to where yesterday's talk ended.
/// </summary>
public sealed class SyncHub(ILogger<SyncHub> log)
{
    private const int MaxMessageBytes = 256 * 1024;
    private const int MaxSocketsPerRoom = 200;
    /// <summary>Messages a single socket may send per second (presenter cursor sharing is the chattiest legitimate source).</summary>
    private const int MaxMessagesPerSecond = 40;
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);

    /// <summary>Receives navigation events while a live session records pacing (set by the session service).</summary>
    public Func<string, int, int, DateTimeOffset, Task>? OnPresenterPosition { get; set; }
    /// <summary>Receives presence changes (viewers, presenters) for the session recorder.</summary>
    public Func<string, int, int, Task>? OnPresence { get; set; }

    private sealed class Room
    {
        public readonly ConcurrentDictionary<Guid, Connection> Sockets = new();
        public readonly ConcurrentDictionary<string, string> LastState = new(StringComparer.Ordinal);
        public string? Screen;
        public int Presenters => Sockets.Values.Count(c => c.CanSend);
        public int Viewers => Sockets.Values.Count(c => !c.CanSend);
    }

    private sealed class Connection(WebSocket socket, bool canSend)
    {
        public WebSocket Socket { get; } = socket;
        public bool CanSend { get; } = canSend;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public long WindowStart;
        public int WindowCount;
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
        var conn = new Connection(socket, canPresent);
        room.Sockets[id] = conn;
        log.LogDebug("Sync join {Slug} by {Principal} (send={CanSend}); {Count} in room", slug, caller.Principal ?? "anonymous", canPresent, room.Sockets.Count);

        try
        {
            // Tell the client what it may do, then replay the latest known state so this window catches up immediately.
            await SendAsync(conn, JsonSerializer.Serialize(new { t = "hello", canSend = canPresent, presenters = room.Presenters, viewers = room.Viewers }), ct);
            foreach (var (_, payload) in room.LastState)
                await SendAsync(conn, payload, ct);
            if (room.Screen is { } screen && !canPresent) await SendAsync(conn, screen, ct);
            await BroadcastPresenceAsync(room, slug, ct);

            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Complete the close handshake so the peer's CloseAsync returns cleanly.
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    await socket.CloseAsync((WebSocketCloseStatus)4413, "message too large", ct);
                    break;
                }
                if (!result.EndOfMessage) continue;

                if (result.MessageType == WebSocketMessageType.Text && canPresent)
                {
                    if (!AllowMessage(conn))
                    {
                        // A misbehaving (or compromised) presenter page cannot flood the room; excess is dropped, not fatal.
                        message.SetLength(0);
                        continue;
                    }
                    var payload = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    var parsed = Validate(payload);
                    switch (parsed.Kind)
                    {
                        case "state":
                            room.LastState[parsed.Channel!] = payload;
                            await BroadcastAsync(room, id, payload, ct);
                            if (parsed.Page is { } sp && OnPresenterPosition is { } onPos)
                                await Safe(() => onPos(slug, sp, parsed.Clicks ?? 0, DateTimeOffset.UtcNow));
                            break;
                        case "info":
                            // Current position reported by a presenting instance; kept so a remote joining later knows where we are.
                            room.LastState["\u0000info"] = payload;
                            await BroadcastAsync(room, id, payload, ct);
                            if (parsed.Page is { } ip && OnPresenterPosition is { } onPos2)
                                await Safe(() => onPos2(slug, ip, parsed.Clicks ?? 0, DateTimeOffset.UtcNow));
                            break;
                        case "nav":
                            await BroadcastAsync(room, id, payload, ct);
                            break;
                        case "screen":
                            // Blackout / message for the audience. "none" clears it. Newcomers get the current screen on join.
                            room.Screen = parsed.Channel == "none" ? null : payload;
                            await BroadcastAsync(room, id, payload, ct);
                            break;
                    }
                }
                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { /* client went away */ }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            room.Sockets.TryRemove(id, out _);
            conn.Lock.Dispose();
            if (canPresent && room.Presenters == 0)
            {
                // No presenter left: forget where they were so later visitors start from the top, and lift any blackout.
                room.LastState.Clear();
                if (room.Screen is not null)
                {
                    room.Screen = null;
                    await Safe(() => BroadcastAsync(room, Guid.Empty, JsonSerializer.Serialize(new { t = "screen", mode = "none" }), CancellationToken.None));
                }
            }
            if (room.Sockets.IsEmpty) _rooms.TryRemove(slug, out _);
            else await Safe(() => BroadcastPresenceAsync(room, slug, CancellationToken.None));
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

    /// <summary>Connected presenters/viewers per deck, for the session recorder and the deploy guard.</summary>
    public (int Presenters, int Viewers) Presence(string slug)
        => _rooms.TryGetValue(slug, out var room) ? (room.Presenters, room.Viewers) : (0, 0);

    /// <summary>Decks that currently have at least one presenter connected.</summary>
    public IReadOnlyList<string> RoomsWithPresenters() => _rooms.Where(kv => kv.Value.Presenters > 0).Select(kv => kv.Key).ToList();

    private static bool AllowMessage(Connection c)
    {
        var now = Environment.TickCount64;
        if (now - c.WindowStart >= 1000) { c.WindowStart = now; c.WindowCount = 0; }
        return ++c.WindowCount <= MaxMessagesPerSecond;
    }

    private async Task BroadcastPresenceAsync(Room room, string slug, CancellationToken ct)
    {
        var presenters = room.Presenters;
        var viewers = room.Viewers;
        await BroadcastAsync(room, Guid.Empty, JsonSerializer.Serialize(new { t = "presence", presenters, viewers }), ct);
        if (OnPresence is { } onPresence) await Safe(() => onPresence(slug, presenters, viewers));
    }

    private async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Sync side effect failed"); }
    }

    private static readonly HashSet<string> NavActions = new(StringComparer.Ordinal) { "next", "prev", "first", "last", "go", "nextSlide", "prevSlide" };
    private static readonly HashSet<string> ScreenModes = new(StringComparer.Ordinal) { "none", "black", "message" };

    private readonly record struct Parsed(string? Kind, string? Channel, int? Page = null, int? Clicks = null);

    /// <summary>
    /// Accepts exactly four message shapes from presenting sockets:
    ///   {"t":"state","channel":string,"state":object}                      Slidev shared/drawing state
    ///   {"t":"nav","action":string,"page"?:int}                           remote-control command
    ///   {"t":"info","page":int,"total":int,"clicks":int,"clicksTotal":int} position report
    ///   {"t":"screen","mode":"none"|"black"|"message","text"?:string}      audience overlay
    /// Anything else is dropped. For state/info the page/clicks are extracted for the session recorder.
    /// </summary>
    private static Parsed Validate(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return default;
            switch (t.GetString())
            {
                case "state":
                {
                    if (!root.TryGetProperty("channel", out var c) || c.ValueKind != JsonValueKind.String) return default;
                    if (!root.TryGetProperty("state", out var s) || s.ValueKind != JsonValueKind.Object) return default;
                    var channel = c.GetString();
                    if (string.IsNullOrEmpty(channel) || channel.Length > 200) return default;
                    int? page = null, clicks = null;
                    if (s.TryGetProperty("page", out var sp) && sp.ValueKind == JsonValueKind.Number && sp.TryGetInt32(out var spn) && spn is >= 1 and <= 10_000) page = spn;
                    if (s.TryGetProperty("clicks", out var sc) && sc.ValueKind == JsonValueKind.Number && sc.TryGetInt32(out var scn) && scn is >= 0 and <= 100_000) clicks = scn;
                    return new("state", channel, page, clicks);
                }
                case "nav":
                    if (!root.TryGetProperty("action", out var a) || a.ValueKind != JsonValueKind.String || !NavActions.Contains(a.GetString()!)) return default;
                    if (root.TryGetProperty("page", out var p) && (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var pn) || pn < 1 || pn > 10_000)) return default;
                    return new("nav", null);
                case "info":
                {
                    var values = new Dictionary<string, int>(4);
                    foreach (var f in new[] { "page", "total", "clicks", "clicksTotal" })
                    {
                        if (!root.TryGetProperty(f, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < 0 || n > 100_000) return default;
                        values[f] = n;
                    }
                    return new("info", null, values["page"] >= 1 ? values["page"] : null, values["clicks"]);
                }
                case "screen":
                {
                    if (!root.TryGetProperty("mode", out var m) || m.ValueKind != JsonValueKind.String || !ScreenModes.Contains(m.GetString()!)) return default;
                    if (root.TryGetProperty("text", out var txt) && (txt.ValueKind != JsonValueKind.String || txt.GetString()!.Length > 300)) return default;
                    return new("screen", m.GetString());
                }
                default:
                    return default;
            }
        }
        catch (JsonException) { return default; }
    }

    private async Task BroadcastAsync(Room room, Guid sender, string payload, CancellationToken ct)
    {
        foreach (var (id, conn) in room.Sockets)
        {
            if (id == sender) continue;
            try { await SendAsync(conn, payload, ct); }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // A dead peer must never take the sender down with it.
                room.Sockets.TryRemove(id, out _);
            }
        }
    }

    private static async Task SendAsync(Connection conn, string payload, CancellationToken ct)
    {
        if (conn.Socket.State != WebSocketState.Open) return;
        await conn.Lock.WaitAsync(ct);
        try { await conn.Socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct); }
        finally { conn.Lock.Release(); }
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
