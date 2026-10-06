using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Podium.Core.Models;
using Podium.Core.Security;
using Podium.Core.Services;
using Podium.Web.Configuration;
using Podium.Web.Security;

namespace Podium.Web.Sync;

/// <summary>
/// Relays Slidev shared state between all open instances of a deck. One room per deck. Only sockets belonging to the
/// owner (or Present grantees) may publish; everyone the access policy admits may listen. The last state per channel
/// is replayed to newcomers so a freshly opened audience window jumps to the presenter's current slide.
///
/// Protocol v3. Presenting sockets declare a <em>role</em>: <c>presenter</c> (the Slidev presenter view), <c>play</c>
/// (a presenting deck window, e.g. the projector) or <c>remote</c> (the phone). Commands that must run exactly once
/// (<c>nav</c>, <c>pointer</c>, <c>timer</c>) are delivered to the <em>primary</em> deck window only: the presenter
/// view when one is connected, otherwise the longest-connected play window; every other window follows through the
/// relayed shared state, so two windows never execute the same "next" independently.
///
/// Besides the raw relay the hub keeps light room metadata: how many presenters, deck windows, remotes and viewers
/// are connected (<c>presence</c>), a presenter-raised <c>screen</c> (blackout / message, replayed to every newcomer;
/// each client decides whether its own window should go dark) and the live <c>session</c> (full details for
/// presenting sockets, a bare "live" flag for viewers). When a session ends, viewers admitted through its join link
/// are disconnected with close code 4410. Cached presenter state is dropped when the last presenter leaves, so a
/// viewer opening the deck the next day is not teleported to where yesterday's talk ended.
/// </summary>
public sealed class SyncHub(ILogger<SyncHub> log, IOptions<PodiumOptions> options, AudienceService audience)
{
    private const int MaxMessageBytes = 256 * 1024;
    private const int MaxSocketsPerRoom = 200;
    /// <summary>Messages a single presenting socket may send per second (presenter cursor sharing is the chattiest legitimate source).</summary>
    private const int MaxMessagesPerSecond = 40;
    /// <summary>Messages a viewer socket may send per second (handshake, reactions, questions, votes; each has its own tighter cap in the audience service).</summary>
    private const int MaxViewerMessagesPerSecond = 10;
    /// <summary>Close code sent to sockets whose admission died with the live session (or a revoked link).</summary>
    public const int CloseSessionEnded = 4410;
    private const string InfoKey = "\u0000info";

    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private long _joinCounter;

    /// <summary>Receives navigation events while a live session records pacing (set by the session service).</summary>
    public Func<string, int, int, DateTimeOffset, Task>? OnPresenterPosition { get; set; }
    /// <summary>Receives presence changes (viewers, presenters) for the session recorder.</summary>
    public Func<string, int, int, Task>? OnPresence { get; set; }
    /// <summary>Looks up the live session of a deck when a room is created (so a server restart does not lose it).</summary>
    public Func<string, Task<Session?>>? ResolveLiveSession { get; set; }

    private sealed class Room
    {
        public readonly ConcurrentDictionary<Guid, Connection> Sockets = new();
        public readonly ConcurrentDictionary<string, string> LastState = new(StringComparer.Ordinal);
        public string? Screen;
        public volatile Session? Session;
        /// <summary>Last slide a presenting window reported (questions and polls are tagged with it).</summary>
        public volatile int CurrentPage;
        public int Presenters => Sockets.Values.Count(c => c.CanSend);
        public int Viewers => Sockets.Values.Count(c => !c.CanSend);
        public int Windows => Sockets.Values.Count(c => c.CanSend && !c.IsRemote);
        public int Remotes => Sockets.Values.Count(c => c.CanSend && c.IsRemote);

        /// <summary>The one deck window that executes commands: the presenter view if any, else the oldest play window.</summary>
        public KeyValuePair<Guid, Connection>? Primary()
        {
            KeyValuePair<Guid, Connection>? best = null;
            foreach (var kv in Sockets)
            {
                var c = kv.Value;
                if (!c.CanSend || c.IsRemote || c.Socket.State != WebSocketState.Open) continue;
                if (best is null) { best = kv; continue; }
                var b = best.Value.Value;
                if (c.IsPresenterView != b.IsPresenterView) { if (c.IsPresenterView) best = kv; continue; }
                if (c.Order < b.Order) best = kv;
            }
            return best;
        }
    }

    private sealed class Connection(WebSocket socket, bool canSend, string? linkId, long order)
    {
        public WebSocket Socket { get; } = socket;
        public bool CanSend { get; } = canSend;
        /// <summary>Share link that admitted this socket (null for owner, grantees, public viewers).</summary>
        public string? LinkId { get; } = linkId;
        public long Order { get; } = order;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        /// <summary>presenter | play | remote (presenting sockets only; null for legacy addon builds, treated as play).</summary>
        public volatile string? Role;
        /// <summary>Stable per-browser id a viewer may announce (used by audience features for one-vote-per-client).</summary>
        public volatile string? ClientId;
        public bool IsRemote => Role == "remote";
        public bool IsPresenterView => Role == "presenter";
        public long WindowStart;
        public int WindowCount;
    }

    public async Task HandleAsync(HttpContext http, string slug, Caller caller, bool canPresent, CancellationToken ct, string? linkId = null)
    {
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var clientAddress = http.Connection.RemoteIpAddress?.ToString();
        var created = false;
        var room = _rooms.GetOrAdd(slug, _ => { created = true; return new Room(); });
        if (created && ResolveLiveSession is { } resolve)
            await Safe(async () => { room.Session = await resolve(slug); if (room.Session is { EndedAt: null } s) audience.Attach(slug, s); });
        if (room.Sockets.Count >= MaxSocketsPerRoom)
        {
            await socket.CloseAsync((WebSocketCloseStatus)4429, "room full", ct);
            return;
        }

        var id = Guid.NewGuid();
        var conn = new Connection(socket, canPresent, linkId, Interlocked.Increment(ref _joinCounter));
        room.Sockets[id] = conn;
        log.LogDebug("Sync join {Slug} by {Principal} (send={CanSend}); {Count} in room", slug, caller.Principal ?? "anonymous", canPresent, room.Sockets.Count);

        try
        {
            // Tell the client what it may do, then replay the latest known state so this window catches up immediately.
            await SendAsync(conn, JsonSerializer.Serialize(new { t = "hello", canSend = canPresent, presenters = room.Presenters, viewers = room.Viewers, windows = room.Windows, remotes = room.Remotes, protocol = 3 }), ct);
            foreach (var (_, payload) in room.LastState)
                await SendAsync(conn, payload, ct);
            if (room.Screen is { } screen) await SendAsync(conn, screen, ct);
            if (room.Session is { EndedAt: null } live)
            {
                await SendAsync(conn, SessionPayload(live, canPresent, replay: true), ct);
                await SendAsync(conn, audience.StatePayload(live), ct);
                var (questions, poll) = audience.ReplayPayloads(slug, canPresent);
                if (questions is not null) await SendAsync(conn, questions, ct);
                if (poll is not null) await SendAsync(conn, poll, ct);
            }
            await BroadcastPresenceAsync(room, slug, ct);

            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Complete the close handshake so the peer's CloseAsync returns cleanly (unless we already closed our side).
                    if (socket.State == WebSocketState.CloseReceived)
                    {
                        try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException) { }
                    }
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    await socket.CloseAsync((WebSocketCloseStatus)4413, "message too large", ct);
                    break;
                }
                if (!result.EndOfMessage) continue;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (!AllowMessage(conn))
                    {
                        // A misbehaving (or compromised) page cannot flood the room; excess is dropped, not fatal.
                        message.SetLength(0);
                        continue;
                    }
                    var payload = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    var parsed = Validate(payload, canPresent);
                    switch (parsed.Kind)
                    {
                        case "hi":
                            // Handshake: presenting sockets declare their role, anyone may announce a client id.
                            if (canPresent && parsed.Role is not null)
                            {
                                conn.Role = parsed.Role;
                                await BroadcastPresenceAsync(room, slug, ct);
                            }
                            if (parsed.ClientId is not null) conn.ClientId = parsed.ClientId;
                            break;
                        case "state":
                            room.LastState[parsed.Channel!] = payload;
                            await BroadcastAsync(room, id, payload, ct);
                            if (parsed.Page is { } sp)
                            {
                                room.CurrentPage = sp;
                                if (OnPresenterPosition is { } onPos) await Safe(() => onPos(slug, sp, parsed.Clicks ?? 0, DateTimeOffset.UtcNow));
                            }
                            break;
                        case "info":
                            // Current position reported by a presenting deck window; kept so a remote joining later knows where we are.
                            if (parsed.Role is not null && conn.Role != parsed.Role)
                            {
                                conn.Role = parsed.Role;
                                await BroadcastPresenceAsync(room, slug, ct);
                            }
                            room.LastState[InfoKey] = payload;
                            await BroadcastAsync(room, id, payload, ct);
                            if (parsed.Page is { } ip)
                            {
                                room.CurrentPage = ip;
                                if (OnPresenterPosition is { } onPos2) await Safe(() => onPos2(slug, ip, parsed.Clicks ?? 0, DateTimeOffset.UtcNow));
                            }
                            break;
                        case "nav":
                        case "pointer":
                        case "timer":
                            // Exactly-once commands: the primary deck window executes, everyone else follows the relayed state.
                            // Deck windows never send these, so a socket without a declared role that does is a remote
                            // (older remote pages that predate the handshake) and must never be chosen as primary itself.
                            if (conn.Role is null) { conn.Role = "remote"; await BroadcastPresenceAsync(room, slug, ct); }
                            if (room.Primary() is { } primary && primary.Key != id)
                                await SendSafeAsync(room, primary.Key, primary.Value, payload, ct);
                            break;
                        case "screen":
                            // Blackout / message. "none" clears it. Every newcomer gets the current screen; clients decide
                            // whether their window should go dark (deck windows do, the presenter view and the remote do not).
                            room.Screen = parsed.Channel == "none" ? null : payload;
                            await BroadcastAsync(room, id, payload, ct);
                            break;
                        case "audience":
                            // Room input (react / question / upvote / vote) or presenter moderation (question ops, polls);
                            // the audience service validates, bounds and broadcasts. Drops are silent by design.
                            await Safe(async () =>
                            {
                                using var doc = JsonDocument.Parse(payload);
                                if (canPresent) await audience.HandlePresenterAsync(slug, room.Session, room.CurrentPage, doc.RootElement);
                                else await audience.HandleViewerAsync(slug, room.Session, conn.ClientId, clientAddress, room.CurrentPage, doc.RootElement);
                            });
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

    /// <summary>
    /// A live session started, changed or ended. Presenting sockets get the details (code, plan, recap); viewers only
    /// learn whether a session is live. When it ends, viewers admitted through the session's link are disconnected.
    /// </summary>
    public async Task NotifySessionAsync(Session session, CancellationToken ct = default)
    {
        AudienceRecap? audienceRecap = null;
        if (session.EndedAt is null) audience.Attach(session.DeckSlug, session);
        else audienceRecap = await audience.FinalizeAsync(session, ct);
        if (!_rooms.TryGetValue(session.DeckSlug, out var room)) return;
        room.Session = session.EndedAt is null ? session : null;
        var presenterPayload = SessionPayload(session, true, audienceRecap: audienceRecap);
        var viewerPayload = SessionPayload(session, false);
        var audiencePayload = audience.StatePayload(room.Session);
        await Task.WhenAll(room.Sockets.Select(kv => SendSafeAsync(room, kv.Key, kv.Value, kv.Value.CanSend ? presenterPayload : viewerPayload, ct)));
        await BroadcastAsync(room, Guid.Empty, audiencePayload, ct);
        if (session.EndedAt is not null && session.LinkId is not null)
            await CloseLinkAsync(session.DeckSlug, session.LinkId, ct);
    }

    /// <summary>Delivery for the audience service: a payload to everyone, to presenters or to viewers of a room.</summary>
    public Task SendToAsync(string slug, AudienceTarget target, string payload, CancellationToken ct = default)
    {
        if (!_rooms.TryGetValue(slug, out var room)) return Task.CompletedTask;
        var sends = room.Sockets
            .Where(kv => target switch { AudienceTarget.Presenters => kv.Value.CanSend, AudienceTarget.Viewers => !kv.Value.CanSend, _ => true })
            .Select(kv => SendSafeAsync(room, kv.Key, kv.Value, payload, ct)).ToList();
        return sends.Count == 0 ? Task.CompletedTask : Task.WhenAll(sends);
    }

    /// <summary>Disconnects every viewer admitted through a share link (the link was revoked or its session ended).</summary>
    public async Task CloseLinkAsync(string slug, string linkId, CancellationToken ct = default)
    {
        if (!_rooms.TryGetValue(slug, out var room)) return;
        var doomed = room.Sockets.Where(kv => !kv.Value.CanSend && kv.Value.LinkId == linkId).ToList();
        foreach (var (id, conn) in doomed)
        {
            room.Sockets.TryRemove(id, out _);
            await Safe(async () =>
            {
                if (conn.Socket.State != WebSocketState.Open) return;
                await conn.Lock.WaitAsync(ct);
                try { await conn.Socket.CloseOutputAsync((WebSocketCloseStatus)CloseSessionEnded, "session ended", ct); }
                finally { conn.Lock.Release(); }
            });
        }
        if (doomed.Count > 0) log.LogInformation("Disconnected {Count} viewers of {Slug} admitted through link {Link}", doomed.Count, slug, linkId);
    }

    /// <summary>Connected presenters/viewers per deck, for the session recorder and the deploy guard.</summary>
    public (int Presenters, int Viewers) Presence(string slug)
        => _rooms.TryGetValue(slug, out var room) ? (room.Presenters, room.Viewers) : (0, 0);

    /// <summary>Decks that currently have at least one presenter connected.</summary>
    public IReadOnlyList<string> RoomsWithPresenters() => _rooms.Where(kv => kv.Value.Presenters > 0).Select(kv => kv.Key).ToList();

    /// <param name="replay">True when sent to a newcomer about a session that was already running (no "started" toast).</param>
    private string SessionPayload(Session s, bool forPresenter, bool replay = false, AudienceRecap? audienceRecap = null)
    {
        var live = s.EndedAt is null;
        if (!forPresenter) return JsonSerializer.Serialize(new { t = "session", live, replay });
        if (live)
        {
            return JsonSerializer.Serialize(new
            {
                t = "session",
                live,
                replay,
                id = s.Id,
                startedAt = s.StartedAt,
                plannedMinutes = s.PlannedMinutes,
                holdDeploys = s.HoldDeploys,
                title = s.Title,
                rehearsal = s.Rehearsal,
                joinCode = s.JoinCode is null ? null : SessionService.FormatJoinCode(s.JoinCode),
                joinUrl = s.JoinCode is null ? null : SessionService.JoinUrl(options.Value.PublicBaseUrl, s.JoinCode),
                serverTime = DateTimeOffset.UtcNow,
            });
        }
        var a = audienceRecap ?? s.AudienceRecap;
        return JsonSerializer.Serialize(new
        {
            t = "session",
            live,
            id = s.Id,
            reason = s.EndReason,
            rehearsal = s.Rehearsal,
            recap = s.Recap is null ? null : new { durationSeconds = s.Recap.DurationSeconds, peakViewers = s.Recap.PeakViewers, slidesVisited = s.Recap.SlidesVisited },
            audience = a is null ? null : new { reactions = a.ReactionTotal, questions = a.Questions.Count, polls = a.Polls.Count },
        });
    }

    private static bool AllowMessage(Connection c)
    {
        var now = Environment.TickCount64;
        if (now - c.WindowStart >= 1000) { c.WindowStart = now; c.WindowCount = 0; }
        return ++c.WindowCount <= (c.CanSend ? MaxMessagesPerSecond : MaxViewerMessagesPerSecond);
    }

    private async Task BroadcastPresenceAsync(Room room, string slug, CancellationToken ct)
    {
        var presenters = room.Presenters;
        var viewers = room.Viewers;
        await BroadcastAsync(room, Guid.Empty, JsonSerializer.Serialize(new { t = "presence", presenters, viewers, windows = room.Windows, remotes = room.Remotes }), ct);
        if (OnPresence is { } onPresence) await Safe(() => onPresence(slug, presenters, viewers));
    }

    private async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Sync side effect failed"); }
    }

    private static readonly HashSet<string> NavActions = new(StringComparer.Ordinal) { "next", "prev", "first", "last", "go", "nextSlide", "prevSlide" };
    private static readonly HashSet<string> ScreenModes = new(StringComparer.Ordinal) { "none", "black", "message" };
    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal) { "presenter", "play", "remote" };
    private static readonly HashSet<string> TimerOps = new(StringComparer.Ordinal) { "start", "pause", "reset", "toggle" };

    private readonly record struct Parsed(string? Kind, string? Channel, int? Page = null, int? Clicks = null, string? Role = null, string? ClientId = null);

    /// <summary>
    /// Accepts exactly these message shapes. From any socket:
    ///   {"t":"hi","role"?:"presenter"|"play"|"remote","cid"?:string}        handshake (role honoured for presenters only)
    /// From presenting sockets additionally:
    ///   {"t":"state","channel":string,"state":object}                        Slidev shared/drawing state
    ///   {"t":"nav","action":string,"page"?:int}                             remote-control command
    ///   {"t":"info","page":int,"total":int,"clicks":int,"clicksTotal":int,"role"?:string}  position report
    ///   {"t":"screen","mode":"none"|"black"|"message","text"?:string}        audience overlay
    ///   {"t":"pointer","x":number|null,"y":number|null}                      laser pointer in slide percent (null clears)
    ///   {"t":"timer","op":"start"|"pause"|"reset"|"toggle"}                 presenter timer control
    /// Anything else is dropped. For state/info the page/clicks are extracted for the session recorder.
    /// </summary>
    private static Parsed Validate(string payload, bool canPresent)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return default;
            var kind = t.GetString();
            if (kind == "hi")
            {
                string? role = null, cid = null;
                if (root.TryGetProperty("role", out var r))
                {
                    if (r.ValueKind != JsonValueKind.String || !Roles.Contains(r.GetString()!)) return default;
                    role = r.GetString();
                }
                if (root.TryGetProperty("cid", out var c))
                {
                    if (c.ValueKind != JsonValueKind.String) return default;
                    var v = c.GetString()!;
                    if (v.Length is < 8 or > 40 || !v.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) return default;
                    cid = v;
                }
                return new("hi", null, Role: role, ClientId: cid);
            }
            // Room input is the only other thing a viewer may send; shapes and limits are checked by the audience service.
            if (!canPresent) return kind is "react" or "question" or "upvote" or "vote" ? new("audience", null) : default;
            if (kind is "poll" || (kind is "question" && root.TryGetProperty("op", out _))) return new("audience", null);
            switch (kind)
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
                    string? role = null;
                    if (root.TryGetProperty("role", out var r))
                    {
                        if (r.ValueKind != JsonValueKind.String || !Roles.Contains(r.GetString()!) || r.GetString() == "remote") return default;
                        role = r.GetString();
                    }
                    return new("info", null, values["page"] >= 1 ? values["page"] : null, values["clicks"], role);
                }
                case "screen":
                {
                    if (!root.TryGetProperty("mode", out var m) || m.ValueKind != JsonValueKind.String || !ScreenModes.Contains(m.GetString()!)) return default;
                    if (root.TryGetProperty("text", out var txt) && (txt.ValueKind != JsonValueKind.String || txt.GetString()!.Length > 300)) return default;
                    return new("screen", m.GetString());
                }
                case "pointer":
                {
                    if (!root.TryGetProperty("x", out var x) || !root.TryGetProperty("y", out var y)) return default;
                    if (x.ValueKind == JsonValueKind.Null && y.ValueKind == JsonValueKind.Null) return new("pointer", null);
                    if (x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number) return default;
                    var xv = x.GetDouble();
                    var yv = y.GetDouble();
                    if (!double.IsFinite(xv) || !double.IsFinite(yv) || xv is < 0 or > 100 || yv is < 0 or > 100) return default;
                    return new("pointer", null);
                }
                case "timer":
                    if (!root.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String || !TimerOps.Contains(op.GetString()!)) return default;
                    return new("timer", op.GetString());
                default:
                    return default;
            }
        }
        catch (JsonException) { return default; }
    }

    private async Task BroadcastAsync(Room room, Guid sender, string payload, CancellationToken ct)
    {
        // Sends run concurrently so one stalled peer delays nobody else; each connection's lock keeps its frames ordered.
        var sends = new List<Task>();
        foreach (var (id, conn) in room.Sockets)
        {
            if (id == sender) continue;
            sends.Add(SendSafeAsync(room, id, conn, payload, ct));
        }
        if (sends.Count > 0) await Task.WhenAll(sends);
    }

    private static async Task SendSafeAsync(Room room, Guid id, Connection conn, string payload, CancellationToken ct)
    {
        try { await SendAsync(conn, payload, ct); }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A dead peer must never take the sender down with it.
            room.Sockets.TryRemove(id, out _);
        }
    }

    private static async Task SendAsync(Connection conn, string payload, CancellationToken ct)
    {
        if (conn.Socket.State != WebSocketState.Open) return;
        await conn.Lock.WaitAsync(ct);
        try
        {
            if (conn.Socket.State == WebSocketState.Open)
                await conn.Socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
        }
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

            await hub.HandleAsync(http, slug, caller, result.CanPresent, http.RequestAborted, result.LinkId);
            return Results.Empty;
        });
        return app;
    }
}
