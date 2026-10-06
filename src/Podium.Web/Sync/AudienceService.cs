using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Web.Sync;

public sealed class AudienceOptions
{
    public const string Section = "Podium:Audience";
    /// <summary>Global kill switch: when off, no reaction, question or vote is accepted anywhere.</summary>
    public bool Enabled { get; set; } = true;
}

public enum AudienceTarget { All, Presenters, Viewers }

/// <summary>
/// The room's side of a live session: reactions, questions with upvotes, polls. One state per deck room, fed by the
/// sync hub with already-authenticated sockets (viewers may only react/ask/upvote/vote; presenters moderate and run
/// polls). Everything is accepted only while a live session runs with the feature switched on and the room not muted,
/// is validated and bounded here (lengths, per-client and per-room rates, one vote per client), is rendered by the
/// clients as text only, is snapshotted into the session once a minute and finalised into its recap at the end.
/// </summary>
public sealed class AudienceService(IOptions<AudienceOptions> options, IServiceScopeFactory scopes, ILogger<AudienceService> log)
{
    public static readonly string[] ReactionKinds = ["clap", "heart", "laugh", "think", "up", "party"];
    private const int MaxQuestionLength = 280, MaxNickLength = 24, MaxQuestionsPerSession = 200, MaxPendingPerClient = 5, MaxPollsPerSession = 20, MaxPollOptions = 6, MaxOptionLength = 60, MaxPollQuestionLength = 200;
    private const int RoomReactionsPerMinute = 2000, RoomQuestionsPerMinute = 50, ClientsPerIp = 20, ClientUpvotesPerMinute = 30;
    private static readonly TimeSpan QuestionCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(700);

    /// <summary>Set by the hub: delivers a payload to a target group of a room.</summary>
    public Func<string, AudienceTarget, string, Task>? Send { get; set; }
    public bool Enabled => options.Value.Enabled;

    private readonly ConcurrentDictionary<string, RoomState> _rooms = new(StringComparer.Ordinal);

    private sealed class ClientState
    {
        public double ReactionTokens = 3;
        public long ReactionRefillAt = Environment.TickCount64;
        public long LastQuestionAt = -1;
        public int UpvotesThisMinute; public long UpvotesMinuteStart;
    }
    private sealed class QuestionState
    {
        public required string Id; public required string Text; public string? Nick; public DateTimeOffset At; public int Slide; public string? Cid;
        public readonly HashSet<string> Upvoters = new(StringComparer.Ordinal); public int RestoredUpvotes;
        public bool Answered, Dismissed, Pinned;
        public int Upvotes => RestoredUpvotes + Upvoters.Count;
    }
    private sealed class PollState
    {
        public required string Id; public required string Question; public required List<string> Options; public DateTimeOffset CreatedAt; public int Slide;
        public readonly Dictionary<string, int> Votes = new(StringComparer.Ordinal);
        /// <summary>Per-option counts carried over from a snapshot (their voters are known only by id).</summary>
        public readonly Dictionary<int, int> RestoredCounts = new();
        public bool Open = true, Shown;
        /// <summary>Voters may replace their answer while open; otherwise the first answer is final.</summary>
        public bool AllowChange;
    }
    private sealed class RoomState
    {
        public readonly object Gate = new();
        public string? SessionId;
        public AudienceSettings Settings = AudienceSettings.Default;
        public bool Muted;
        public readonly Dictionary<string, int> Reactions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Pending = new(StringComparer.Ordinal);
        public readonly List<QuestionState> Questions = [];
        public readonly List<PollState> Polls = [];
        public readonly Dictionary<string, ClientState> Clients = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<string>> CidsByIp = new(StringComparer.Ordinal);
        public long ReactionsMinuteStart; public int ReactionsThisMinute;
        public long QuestionsMinuteStart; public int QuestionsThisMinute;
        public bool FlushScheduled;
        public bool Dirty;
    }

    // ---- Session lifecycle -------------------------------------------------------------------------------------------

    /// <summary>The hub learned (or re-learned) the live session of a room: adopt its settings and restore any snapshot.</summary>
    public void Attach(string slug, Session session)
    {
        var room = _rooms.GetOrAdd(slug, _ => new RoomState());
        lock (room.Gate)
        {
            if (room.SessionId != session.Id)
            {
                Reset(room);
                room.SessionId = session.Id;
                if (session.AudienceRecap is { } recap) Restore(room, recap);
            }
            room.Settings = session.Audience ?? AudienceSettings.Default;
            room.Muted = session.AudienceMuted;
        }
    }

    private static void Reset(RoomState room)
    {
        room.Reactions.Clear(); room.Pending.Clear(); room.Questions.Clear(); room.Polls.Clear(); room.Clients.Clear(); room.CidsByIp.Clear();
        room.Dirty = false;
    }

    private static void Restore(RoomState room, AudienceRecap recap)
    {
        foreach (var (k, v) in recap.Reactions) room.Reactions[k] = v;
        foreach (var q in recap.Questions.Take(MaxQuestionsPerSession))
            room.Questions.Add(new QuestionState { Id = q.Id, Text = q.Text, Nick = q.Nick, At = q.At, Slide = q.Slide, RestoredUpvotes = q.Upvotes, Answered = q.Answered, Dismissed = q.Dismissed, Pinned = q.Pinned });
        foreach (var p in recap.Polls.Take(MaxPollsPerSession))
        {
            var ps = new PollState { Id = p.Id, Question = p.Question, Options = p.Options.Select(o => o.Text).ToList(), CreatedAt = p.CreatedAt, Slide = p.Slide, Open = p.Open, Shown = p.Shown, AllowChange = p.AllowChange };
            // Counts survive a restart; who voted for what does not, so restored voters are pinned to a sentinel option
            // that keeps them from voting twice while the totals are carried separately.
            foreach (var v in p.Voters) ps.Votes[v] = -1;
            for (var i = 0; i < ps.Options.Count; i++) ps.RestoredCounts[i] = p.Options[i].Votes;
            room.Polls.Add(ps);
        }
    }

    /// <summary>Everything the room contributed so far (also what gets persisted).</summary>
    public AudienceRecap Snapshot(string slug)
    {
        if (!_rooms.TryGetValue(slug, out var room)) return AudienceRecap.Empty;
        lock (room.Gate) return BuildRecap(room);
    }

    private static AudienceRecap BuildRecap(RoomState room) => new(
        new Dictionary<string, int>(room.Reactions),
        room.Questions.Select(q => new AudienceQuestion(q.Id, q.Text, q.Nick, q.At, q.Slide, q.Upvotes, q.Answered, q.Dismissed, q.Pinned)).ToList(),
        room.Polls.Select(p => { var results = OptionResults(p); return new AudiencePoll(p.Id, p.Question, results, p.CreatedAt, p.Slide, p.Open, p.Shown, results.Sum(o => o.Votes), p.Votes.Keys.ToList(), p.AllowChange); }).ToList());

    private static IReadOnlyList<PollOption> OptionResults(PollState p)
        => p.Options.Select((text, i) => new PollOption(text, (p.RestoredCounts.TryGetValue(i, out var r) ? r : 0) + p.Votes.Values.Count(v => v == i))).ToList();

    /// <summary>Maintenance tick: persist rooms that changed since the last snapshot (a restart then loses at most a minute).</summary>
    public async Task SnapshotAllAsync(CancellationToken ct = default)
    {
        foreach (var (slug, room) in _rooms)
        {
            AudienceRecap recap; string? sessionId;
            lock (room.Gate) { if (!room.Dirty || room.SessionId is null) continue; room.Dirty = false; recap = BuildRecap(room); sessionId = room.SessionId; }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ISessionStore>();
                if (await store.GetAsync(slug, sessionId, ct) is { EndedAt: null } s) await store.UpsertAsync(s with { AudienceRecap = recap }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Audience snapshot for {Deck} failed", slug); lock (room.Gate) room.Dirty = true; }
        }
    }

    /// <summary>The session ended: write the final recap into it and forget the room.</summary>
    public async Task<AudienceRecap?> FinalizeAsync(Session ended, CancellationToken ct = default)
    {
        if (!_rooms.TryRemove(ended.DeckSlug, out var room)) return ended.AudienceRecap;
        AudienceRecap recap;
        lock (room.Gate) { if (room.SessionId != ended.Id) return ended.AudienceRecap; recap = BuildRecap(room); }
        if (recap.Questions.Count == 0 && recap.Polls.Count == 0 && recap.ReactionTotal == 0) return ended.AudienceRecap;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ISessionStore>();
            if (await store.GetAsync(ended.DeckSlug, ended.Id, ct) is { } s) await store.UpsertAsync(s with { AudienceRecap = recap }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Audience recap for {Deck} could not be saved", ended.DeckSlug); }
        return recap;
    }

    // ---- Payloads ------------------------------------------------------------------------------------------------------

    /// <summary>What the room may do right now; sent to everyone on join and whenever the session changes.</summary>
    public string StatePayload(Session? session)
    {
        var live = session is { EndedAt: null };
        var s = session?.Audience ?? AudienceSettings.Default;
        var on = Enabled && live;
        return JsonSerializer.Serialize(new
        {
            t = "audience",
            live,
            muted = session?.AudienceMuted ?? false,
            settings = new { reactions = on && s.Reactions, questions = on && s.Questions, polls = on && s.Polls, floatReactions = s.FloatReactions, nicknames = s.Nicknames },
        });
    }

    /// <summary>Current questions, current poll and (presenters only) the poll history for a newcomer; null when nothing to replay.</summary>
    public (string? Questions, string? Poll, string? Polls) ReplayPayloads(string slug, bool forPresenter)
    {
        if (!_rooms.TryGetValue(slug, out var room)) return (null, null, null);
        lock (room.Gate)
        {
            var q = room.Questions.Count > 0 ? QuestionsPayload(room) : null;
            var p = CurrentPoll(room) is { } poll ? PollPayload(poll, forPresenter) : null;
            var all = forPresenter && room.Polls.Count > 0 ? PollsPayload(room) : null;
            return (q, p, all);
        }
    }

    /// <summary>Every poll of the session with full results, newest first (presenters only: re-view, re-show, reopen).</summary>
    private static string PollsPayload(RoomState room) => JsonSerializer.Serialize(new
    {
        t = "polls",
        items = Enumerable.Reverse(room.Polls).Select(p =>
        {
            var results = OptionResults(p);
            return new { id = p.Id, question = p.Question, open = p.Open, shown = p.Shown, allowChange = p.AllowChange, createdAt = p.CreatedAt, slide = p.Slide, options = results.Select(o => new { text = o.Text, votes = o.Votes }), total = results.Sum(o => o.Votes) };
        }),
    });

    private static string QuestionsPayload(RoomState room) => JsonSerializer.Serialize(new
    {
        t = "questions",
        items = room.Questions.Where(q => !q.Dismissed).OrderByDescending(q => q.Pinned).ThenByDescending(q => q.Upvotes).ThenBy(q => q.At).Take(100)
            .Select(q => new { id = q.Id, text = q.Text, nick = q.Nick, at = q.At, slide = q.Slide, upvotes = q.Upvotes, answered = q.Answered, pinned = q.Pinned }),
        total = room.Questions.Count(q => !q.Dismissed),
    });

    private static PollState? CurrentPoll(RoomState room) => room.Polls.LastOrDefault(p => p.Open || p.Shown);

    private static string PollPayload(PollState? p, bool forPresenter)
    {
        if (p is null) return JsonSerializer.Serialize(new { t = "poll", poll = (object?)null });
        var results = OptionResults(p);
        var reveal = forPresenter || p.Shown || !p.Open;
        return JsonSerializer.Serialize(new
        {
            t = "poll",
            poll = new
            {
                id = p.Id, question = p.Question, open = p.Open, shown = p.Shown, allowChange = p.AllowChange, createdAt = p.CreatedAt, slide = p.Slide,
                options = results.Select(o => reveal ? new { text = o.Text, votes = (int?)o.Votes } : new { text = o.Text, votes = (int?)null }),
                total = reveal ? (int?)results.Sum(o => o.Votes) : null,
            },
        });
    }

    private static string ReactionsPayload(RoomState room, Dictionary<string, int> burst) => JsonSerializer.Serialize(new { t = "reactions", counts = burst, totals = new Dictionary<string, int>(room.Reactions) });

    // ---- Viewer input --------------------------------------------------------------------------------------------------

    /// <summary>
    /// A message from a viewer socket. Returns false when it was dropped (unknown shape, feature off, over a limit); the
    /// caller never tells the viewer why. Accepted input is broadcast through <see cref="Send"/>.
    /// </summary>
    public async Task<bool> HandleViewerAsync(string slug, Session? session, string? cid, string? ip, int currentSlide, JsonElement root)
    {
        if (!Enabled || session is not { EndedAt: null } || cid is null) return false;
        if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return false;
        var room = _rooms.GetOrAdd(slug, _ => new RoomState());
        string? broadcastAll = null, broadcastPresenters = null, broadcastViewers = null, broadcastHistory = null;
        lock (room.Gate)
        {
            if (room.SessionId != session.Id) { Reset(room); room.SessionId = session.Id; }
            room.Settings = session.Audience ?? AudienceSettings.Default;
            room.Muted = session.AudienceMuted;
            if (room.Muted) return false;
            if (!AdmitClient(room, cid, ip)) return false;
            var client = room.Clients[cid];
            var now = Environment.TickCount64;
            switch (t.GetString())
            {
                case "react":
                {
                    if (!room.Settings.Reactions) return false;
                    if (!root.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String || Array.IndexOf(ReactionKinds, k.GetString()) < 0) return false;
                    // Per client: a bucket of 3 refilled at 1/s. Per room: 2000/min.
                    client.ReactionTokens = Math.Min(3, client.ReactionTokens + (now - client.ReactionRefillAt) / 1000.0);
                    client.ReactionRefillAt = now;
                    if (client.ReactionTokens < 1) return false;
                    if (now - room.ReactionsMinuteStart >= 60_000) { room.ReactionsMinuteStart = now; room.ReactionsThisMinute = 0; }
                    if (++room.ReactionsThisMinute > RoomReactionsPerMinute) return false;
                    client.ReactionTokens -= 1;
                    var kind = k.GetString()!;
                    room.Reactions[kind] = room.Reactions.GetValueOrDefault(kind) + 1;
                    room.Pending[kind] = room.Pending.GetValueOrDefault(kind) + 1;
                    room.Dirty = true;
                    if (!room.FlushScheduled) { room.FlushScheduled = true; _ = FlushLaterAsync(slug, room); }
                    return true;
                }
                case "question":
                {
                    if (!room.Settings.Questions) return false;
                    if (!root.TryGetProperty("text", out var tx) || tx.ValueKind != JsonValueKind.String) return false;
                    var text = Clean(tx.GetString()!, MaxQuestionLength);
                    if (text.Length < 2) return false;
                    string? nick = null;
                    if (room.Settings.Nicknames && root.TryGetProperty("nick", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        nick = Clean(n.GetString()!, MaxNickLength);
                        if (nick.Length == 0 || IsReservedNick(nick)) nick = null;
                    }
                    if (client.LastQuestionAt >= 0 && now - client.LastQuestionAt < QuestionCooldown.TotalMilliseconds) return false;
                    if (room.Questions.Count(q => q.Cid == cid && !q.Answered && !q.Dismissed) >= MaxPendingPerClient) return false;
                    if (room.Questions.Count >= MaxQuestionsPerSession) return false;
                    if (now - room.QuestionsMinuteStart >= 60_000) { room.QuestionsMinuteStart = now; room.QuestionsThisMinute = 0; }
                    if (++room.QuestionsThisMinute > RoomQuestionsPerMinute) return false;
                    client.LastQuestionAt = now;
                    var q = new QuestionState { Id = NewId(), Text = text, Nick = nick, At = DateTimeOffset.UtcNow, Slide = currentSlide, Cid = cid };
                    q.Upvoters.Add(cid); // asking counts as caring
                    room.Questions.Add(q);
                    room.Dirty = true;
                    broadcastAll = QuestionsPayload(room);
                    break;
                }
                case "upvote":
                {
                    if (!room.Settings.Questions) return false;
                    if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return false;
                    var q = room.Questions.FirstOrDefault(x => x.Id == idEl.GetString() && !x.Dismissed);
                    if (q is null) return false;
                    if (now - client.UpvotesMinuteStart >= 60_000) { client.UpvotesMinuteStart = now; client.UpvotesThisMinute = 0; }
                    if (++client.UpvotesThisMinute > ClientUpvotesPerMinute) return false;
                    // Toggle: a second upvote takes it back.
                    if (!q.Upvoters.Add(cid)) q.Upvoters.Remove(cid);
                    room.Dirty = true;
                    broadcastAll = QuestionsPayload(room);
                    break;
                }
                case "vote":
                {
                    if (!room.Settings.Polls) return false;
                    if (!root.TryGetProperty("poll", out var pid) || pid.ValueKind != JsonValueKind.String) return false;
                    if (!root.TryGetProperty("option", out var op) || op.ValueKind != JsonValueKind.Number || !op.TryGetInt32(out var option)) return false;
                    var poll = room.Polls.FirstOrDefault(p => p.Id == pid.GetString());
                    if (poll is null || !poll.Open || option < 0 || option >= poll.Options.Count) return false;
                    if (poll.Votes.TryGetValue(cid, out var previous))
                    {
                        if (previous < 0) return false;          // voted before a restart; the count is kept
                        if (!poll.AllowChange) return false;     // first answer is final
                        if (previous == option) return false;    // nothing changed
                    }
                    poll.Votes[cid] = option; // with AllowChange a changed mind replaces the earlier vote
                    room.Dirty = true;
                    broadcastPresenters = PollPayload(poll, true);
                    broadcastViewers = PollPayload(poll, false);
                    broadcastHistory = PollsPayload(room);
                    break;
                }
                default:
                    return false;
            }
        }
        if (broadcastAll is not null) await Deliver(slug, AudienceTarget.All, broadcastAll);
        if (broadcastPresenters is not null) await Deliver(slug, AudienceTarget.Presenters, broadcastPresenters);
        if (broadcastViewers is not null) await Deliver(slug, AudienceTarget.Viewers, broadcastViewers);
        if (broadcastHistory is not null) await Deliver(slug, AudienceTarget.Presenters, broadcastHistory);
        return true;
    }

    private static bool AdmitClient(RoomState room, string cid, string? ip)
    {
        if (!room.Clients.ContainsKey(cid))
        {
            // Rotating client ids to vote twice is bounded per address (a room behind one NAT still fits many phones).
            var key = ip ?? "?";
            if (!room.CidsByIp.TryGetValue(key, out var set)) room.CidsByIp[key] = set = new HashSet<string>(StringComparer.Ordinal);
            if (!set.Contains(cid) && set.Count >= ClientsPerIp) return false;
            set.Add(cid);
            room.Clients[cid] = new ClientState();
        }
        return true;
    }

    private async Task FlushLaterAsync(string slug, RoomState room)
    {
        await Task.Delay(FlushInterval);
        Dictionary<string, int> burst;
        string payload;
        lock (room.Gate)
        {
            room.FlushScheduled = false;
            if (room.Pending.Count == 0) return;
            burst = new Dictionary<string, int>(room.Pending);
            room.Pending.Clear();
            payload = ReactionsPayload(room, burst);
        }
        await Deliver(slug, AudienceTarget.All, payload);
    }

    // ---- Presenter moderation and polls ------------------------------------------------------------------------------

    /// <summary>
    /// Accepted from presenting sockets:
    ///   {"t":"question","op":"answer"|"dismiss"|"pin"|"unpin"|"restore","id":string}
    ///   {"t":"poll","op":"create","question":string,"options":[string,...],"allowChange"?:bool}  (opens at once)
    ///   {"t":"poll","op":"close"|"open"|"show"|"hide"|"remove","id":string}
    /// "show" works on any poll of the session, so earlier results can be put back on screen; "open" re-opens voting.
    /// </summary>
    public async Task<bool> HandlePresenterAsync(string slug, Session? session, int currentSlide, JsonElement root)
    {
        if (session is not { EndedAt: null }) return false;
        if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return false;
        if (!root.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String) return false;
        var op = opEl.GetString();
        var room = _rooms.GetOrAdd(slug, _ => new RoomState());
        string? all = null, presenters = null, viewers = null, history = null;
        lock (room.Gate)
        {
            if (room.SessionId != session.Id) { Reset(room); room.SessionId = session.Id; }
            switch (t.GetString())
            {
                case "question":
                {
                    if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return false;
                    var q = room.Questions.FirstOrDefault(x => x.Id == idEl.GetString());
                    if (q is null) return false;
                    switch (op)
                    {
                        case "answer": q.Answered = true; q.Pinned = false; break;
                        case "dismiss": q.Dismissed = true; q.Pinned = false; break;
                        case "restore": q.Dismissed = false; q.Answered = false; break;
                        case "pin": foreach (var o in room.Questions) o.Pinned = false; q.Pinned = true; break;
                        case "unpin": q.Pinned = false; break;
                        default: return false;
                    }
                    room.Dirty = true;
                    all = QuestionsPayload(room);
                    break;
                }
                case "poll":
                {
                    PollState? poll;
                    if (op == "create")
                    {
                        if (room.Polls.Count >= MaxPollsPerSession) return false;
                        if (!root.TryGetProperty("question", out var qEl) || qEl.ValueKind != JsonValueKind.String) return false;
                        var question = Clean(qEl.GetString()!, MaxPollQuestionLength);
                        if (question.Length < 2) return false;
                        var opts = new List<string>();
                        if (root.TryGetProperty("options", out var arr) && arr.ValueKind == JsonValueKind.Array)
                            foreach (var o in arr.EnumerateArray()) { if (o.ValueKind != JsonValueKind.String) return false; var v = Clean(o.GetString()!, MaxOptionLength); if (v.Length > 0) opts.Add(v); }
                        if (opts.Count is < 2 or > MaxPollOptions) return false;
                        var allowChange = root.TryGetProperty("allowChange", out var ac) && ac.ValueKind == JsonValueKind.True;
                        foreach (var p in room.Polls) { p.Open = false; p.Shown = false; } // one poll at a time
                        poll = new PollState { Id = NewId(), Question = question, Options = opts, CreatedAt = DateTimeOffset.UtcNow, Slide = currentSlide, AllowChange = allowChange };
                        room.Polls.Add(poll);
                    }
                    else
                    {
                        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return false;
                        poll = room.Polls.FirstOrDefault(p => p.Id == idEl.GetString());
                        if (poll is null) return false;
                        switch (op)
                        {
                            case "close": poll.Open = false; break;
                            case "open": foreach (var p in room.Polls) { if (p != poll) { p.Open = false; p.Shown = false; } } poll.Open = true; break;
                            // Re-showing an earlier poll takes the stage: it becomes the current poll for everyone, and
                            // an open poll elsewhere is closed so only one poll is ever in front of the room.
                            case "show": foreach (var p in room.Polls) { if (p != poll) { p.Shown = false; p.Open = false; } } poll.Shown = true; break;
                            case "hide": poll.Shown = false; break;
                            case "remove": room.Polls.Remove(poll); poll = CurrentPoll(room); break;
                            default: return false;
                        }
                    }
                    room.Dirty = true;
                    var current = CurrentPoll(room);
                    presenters = PollPayload(current, true);
                    viewers = PollPayload(current, false);
                    history = PollsPayload(room);
                    break;
                }
                default:
                    return false;
            }
        }
        if (all is not null) await Deliver(slug, AudienceTarget.All, all);
        if (presenters is not null) await Deliver(slug, AudienceTarget.Presenters, presenters);
        if (viewers is not null) await Deliver(slug, AudienceTarget.Viewers, viewers);
        if (history is not null) await Deliver(slug, AudienceTarget.Presenters, history);
        return true;
    }

    private async Task Deliver(string slug, AudienceTarget target, string payload)
    {
        if (Send is not { } send) return;
        try { await send(slug, target, payload); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Audience delivery failed for {Deck}", slug); }
    }

    /// <summary>Trims, collapses whitespace, drops control characters, caps the length.</summary>
    internal static string Clean(string s, int max)
    {
        var chars = s.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var collapsed = System.Text.RegularExpressions.Regex.Replace(new string(chars), @"\s+", " ").Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..max];
    }

    private static readonly string[] ReservedNicks = ["presenter", "speaker", "host", "podium", "admin", "moderator", "owner"];
    private static bool IsReservedNick(string nick) => ReservedNicks.Any(r => nick.Contains(r, StringComparison.OrdinalIgnoreCase));

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[9];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
    }
}
