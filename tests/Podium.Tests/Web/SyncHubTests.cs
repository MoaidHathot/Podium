using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Podium.Core.Models;

namespace Podium.Tests.Web;

[Collection(nameof(WebCollection))]
public sealed class SyncHubTests(PodiumWebFactory app)
{
    private async Task<WebSocket> ConnectAsync(string slug, string? cookie, string origin = PodiumWebFactory.PublicOrigin)
    {
        var client = app.Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            r.Headers["Origin"] = origin;
            r.Headers["X-Forwarded-For"] = $"10.9.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}";
            if (cookie is not null) r.Headers["Cookie"] = cookie;
        };
        var uri = new Uri(new Uri(PodiumWebFactory.PublicOrigin.Replace("http", "ws")), $"/ws/sync/{slug}");
        return await client.ConnectAsync(uri, CancellationToken.None);
    }

    private static async Task<JsonDocument?> ReceiveAsync(WebSocket ws, TimeSpan? timeout = null)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            var r = await ws.ReceiveAsync(buffer, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count));
        }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task<JsonDocument> ReceiveUntilAsync(WebSocket ws, string type, int maxMessages = 20)
    {
        for (var i = 0; i < maxMessages; i++)
        {
            var doc = await ReceiveAsync(ws) ?? throw new Xunit.Sdk.XunitException($"socket closed while waiting for '{type}'");
            if (doc.RootElement.GetProperty("t").GetString() == type) return doc;
        }
        throw new Xunit.Sdk.XunitException($"no '{type}' message within {maxMessages} messages");
    }

    private static Task SendAsync(WebSocket ws, object message)
        => ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    private async Task<string> OwnerCookieAsync()
    {
        var raw = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri(PodiumWebFactory.PublicOrigin) });
        var login = await raw.GetAsync("/dev-login?returnUrl=/");
        return login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("podium_session=", StringComparison.Ordinal)).Split(';')[0];
    }

    [Fact]
    public async Task Presenter_drives_viewers_and_presence_is_announced()
    {
        var deck = await app.SeedDeckAsync("sync-deck", Visibility.Public);
        var owner = await OwnerCookieAsync();

        using var viewer = await ConnectAsync(deck.Slug, null);
        var hello = await ReceiveUntilAsync(viewer, "hello");
        Assert.False(hello.RootElement.GetProperty("canSend").GetBoolean());
        Assert.Equal(0, hello.RootElement.GetProperty("presenters").GetInt32());

        using var presenter = await ConnectAsync(deck.Slug, owner);
        var pHello = await ReceiveUntilAsync(presenter, "hello");
        Assert.True(pHello.RootElement.GetProperty("canSend").GetBoolean());

        // The viewer learns a presenter arrived (after the presence announcing its own join).
        JsonDocument presence;
        do { presence = await ReceiveUntilAsync(viewer, "presence"); } while (presence.RootElement.GetProperty("presenters").GetInt32() == 0);
        Assert.Equal(1, presence.RootElement.GetProperty("presenters").GetInt32());
        Assert.Equal(1, presence.RootElement.GetProperty("viewers").GetInt32());

        // Presenter state is relayed; the presenter does not get its own echo.
        await SendAsync(presenter, new { t = "state", channel = "deck - shared", state = new { page = 7, clicks = 2 } });
        var state = await ReceiveUntilAsync(viewer, "state");
        Assert.Equal(7, state.RootElement.GetProperty("state").GetProperty("page").GetInt32());

        // A viewer cannot publish: its message is dropped and never reaches the presenter.
        await SendAsync(viewer, new { t = "state", channel = "deck - shared", state = new { page = 1 } });
        await SendAsync(presenter, new { t = "info", page = 7, total = 20, clicks = 2, clicksTotal = 3 });
        var next = await ReceiveUntilAsync(presenter, "info", 5).ContinueWith(t => t.IsFaulted ? null : t.Result);
        Assert.Null(next); // presenter only ever receives presence/build/screen, never the viewer's forged state or its own info

        // A late viewer gets the cached state on join.
        using var late = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(late, "hello");
        var replay = await ReceiveUntilAsync(late, "state");
        Assert.Equal(7, replay.RootElement.GetProperty("state").GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Screen_overlay_is_relayed_cached_for_newcomers_and_cleared_when_the_presenter_leaves()
    {
        var deck = await app.SeedDeckAsync("sync-screen-deck", Visibility.Public);
        var owner = await OwnerCookieAsync();
        using var presenter = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(presenter, "hello");
        using var viewer = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(viewer, "hello");

        await SendAsync(presenter, new { t = "screen", mode = "message", text = "Demo in progress" });
        var screen = await ReceiveUntilAsync(viewer, "screen");
        Assert.Equal("message", screen.RootElement.GetProperty("mode").GetString());

        using var late = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(late, "hello");
        var cached = await ReceiveUntilAsync(late, "screen");
        Assert.Equal("Demo in progress", cached.RootElement.GetProperty("text").GetString());

        // Oversized / unknown modes are rejected.
        await SendAsync(presenter, new { t = "screen", mode = "message", text = new string('x', 400) });
        await SendAsync(presenter, new { t = "screen", mode = "purple" });

        // Presenter leaves: overlay lifted, cached state forgotten.
        await presenter.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        var cleared = await ReceiveUntilAsync(viewer, "screen");
        Assert.Equal("none", cleared.RootElement.GetProperty("mode").GetString());
        JsonDocument presence;
        do { presence = await ReceiveUntilAsync(viewer, "presence"); } while (presence.RootElement.GetProperty("presenters").GetInt32() != 0);

        using var fresh = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(fresh, "hello");
        var anything = await ReceiveAsync(fresh, TimeSpan.FromMilliseconds(600));
        Assert.True(anything is null || anything.RootElement.GetProperty("t").GetString() == "presence"); // no stale screen/state replay
    }

    [Fact]
    public async Task Flooding_remote_is_throttled_not_disconnected()
    {
        var deck = await app.SeedDeckAsync("sync-flood-deck", Visibility.Public);
        var owner = await OwnerCookieAsync();
        using var window = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(window, "hello");
        await SendAsync(window, new { t = "hi", role = "play" });
        using var remote = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(remote, "hello");
        await SendAsync(remote, new { t = "hi", role = "remote" });
        using var viewer = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(viewer, "hello");

        for (var i = 0; i < 120; i++) await SendAsync(remote, new { t = "nav", action = "next" });
        var received = 0;
        while (await ReceiveAsync(window, TimeSpan.FromMilliseconds(400)) is { } doc)
            if (doc.RootElement.GetProperty("t").GetString() == "nav") received++;
        Assert.InRange(received, 1, 45); // 40/s cap; the rest dropped
        Assert.Equal(WebSocketState.Open, remote.State);

        // Commands are for the deck window only; the audience never sees them.
        var leaked = 0;
        while (await ReceiveAsync(viewer, TimeSpan.FromMilliseconds(300)) is { } doc)
            if (doc.RootElement.GetProperty("t").GetString() == "nav") leaked++;
        Assert.Equal(0, leaked);
    }

    [Fact]
    public async Task Commands_run_once_on_the_presenter_view_when_present_else_on_the_oldest_play_window()
    {
        var deck = await app.SeedDeckAsync("sync-primary-deck", Visibility.Public);
        var owner = await OwnerCookieAsync();
        using var projector = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(projector, "hello");
        await SendAsync(projector, new { t = "info", page = 1, total = 9, clicks = 0, clicksTotal = 0, role = "play" });
        using var laptop = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(laptop, "hello");
        await SendAsync(laptop, new { t = "info", page = 1, total = 9, clicks = 0, clicksTotal = 0, role = "play" });
        using var remote = await ConnectAsync(deck.Slug, owner);
        var hello = await ReceiveUntilAsync(remote, "hello");
        Assert.Equal(3, hello.RootElement.GetProperty("protocol").GetInt32());
        await SendAsync(remote, new { t = "hi", role = "remote" });
        // Presence distinguishes deck windows from remotes so the phone knows whether anything can execute its commands.
        JsonDocument presence;
        do { presence = await ReceiveUntilAsync(remote, "presence"); } while (presence.RootElement.GetProperty("remotes").GetInt32() == 0);
        Assert.Equal(2, presence.RootElement.GetProperty("windows").GetInt32());
        Assert.Equal(1, presence.RootElement.GetProperty("remotes").GetInt32());

        // Two play windows: only the oldest executes.
        await SendAsync(remote, new { t = "nav", action = "next" });
        var onProjector = await ReceiveUntilAsync(projector, "nav");
        Assert.Equal("next", onProjector.RootElement.GetProperty("action").GetString());
        Assert.Null(await ReceiveUntilAsync(laptop, "nav", 6).ContinueWith(t => t.IsFaulted ? null : t.Result));

        // A presenter view joins: it becomes the authority, play windows follow through state.
        using var presenterView = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(presenterView, "hello");
        await SendAsync(presenterView, new { t = "hi", role = "presenter" });
        await SendAsync(remote, new { t = "pointer", x = 42.5, y = 10 });
        await SendAsync(remote, new { t = "timer", op = "toggle" });
        var pointer = await ReceiveUntilAsync(presenterView, "pointer");
        Assert.Equal(42.5, pointer.RootElement.GetProperty("x").GetDouble());
        var timer = await ReceiveUntilAsync(presenterView, "timer");
        Assert.Equal("toggle", timer.RootElement.GetProperty("op").GetString());
        Assert.Null(await ReceiveUntilAsync(projector, "pointer", 6).ContinueWith(t => t.IsFaulted ? null : t.Result));

        // Presenter view gone: the oldest play window is primary again. Invalid commands never reach anyone.
        await presenterView.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await Task.Delay(100);
        await SendAsync(remote, new { t = "pointer", x = 150, y = 10 });      // out of range
        await SendAsync(remote, new { t = "timer", op = "explode" });         // unknown op
        await SendAsync(remote, new { t = "nav", action = "prev" });
        var next = await ReceiveUntilAsync(projector, "nav");
        Assert.Equal("prev", next.RootElement.GetProperty("action").GetString());
        Assert.Null(await ReceiveUntilAsync(projector, "pointer", 3).ContinueWith(t => t.IsFaulted ? null : t.Result));

        // A remote that predates the handshake (no role) is still recognised as a remote, never as the executor.
        using var oldRemote = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(oldRemote, "hello");
        await SendAsync(oldRemote, new { t = "nav", action = "first" });
        Assert.Equal("first", (await ReceiveUntilAsync(projector, "nav")).RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Screen_is_replayed_to_every_newcomer_and_viewers_may_only_say_hi()
    {
        var deck = await app.SeedDeckAsync("sync-screen-all-deck", Visibility.Public);
        var owner = await OwnerCookieAsync();
        using var remote = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(remote, "hello");
        await SendAsync(remote, new { t = "hi", role = "remote" });
        await SendAsync(remote, new { t = "screen", mode = "black" });

        // The projector (a presenting window) opened after the blackout was raised must also go dark.
        using var projector = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(projector, "hello");
        Assert.Equal("black", (await ReceiveUntilAsync(projector, "screen")).RootElement.GetProperty("mode").GetString());

        using var viewer = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(viewer, "hello");
        await ReceiveUntilAsync(viewer, "screen");
        // Viewers may announce a client id but nothing they send is relayed or executed.
        await SendAsync(viewer, new { t = "hi", cid = "abcdefgh12345678" });
        await SendAsync(viewer, new { t = "hi", role = "presenter" });
        await SendAsync(viewer, new { t = "nav", action = "next" });
        await SendAsync(viewer, new { t = "screen", mode = "none" });
        await SendAsync(viewer, new { t = "pointer", x = 1, y = 1 });
        Assert.Null(await ReceiveUntilAsync(projector, "nav", 4).ContinueWith(t => t.IsFaulted ? null : t.Result));
        Assert.Equal(WebSocketState.Open, viewer.State);

        using var late = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(late, "hello");
        Assert.Equal("black", (await ReceiveUntilAsync(late, "screen")).RootElement.GetProperty("mode").GetString()); // the viewer's "none" changed nothing
    }

    [Fact]
    public async Task Session_changes_reach_every_window_and_link_viewers_are_cut_when_it_ends()
    {
        var deck = await app.SeedDeckAsync("sync-session-deck");
        var owner = await OwnerCookieAsync();
        var api = await app.OwnerClientAsync();

        using var presenterView = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(presenterView, "hello");
        await SendAsync(presenterView, new { t = "hi", role = "presenter" });

        var started = await api.PostAsJsonAsync($"/api/decks/{deck.Slug}/sessions", new { plannedMinutes = 30, holdDeploys = false, freeze = false });
        Assert.Equal(System.Net.HttpStatusCode.OK, started.StatusCode);
        var body = await started.Content.ReadFromJsonAsync<JsonElement>();
        var linkId = body.GetProperty("session").GetProperty("linkId").GetString()!;
        var joinCode = body.GetProperty("session").GetProperty("joinCode").GetString()!;

        // Presenting sockets get the details the HUD needs; the plan change follows as it happens.
        var live = await ReceiveUntilAsync(presenterView, "session");
        Assert.True(live.RootElement.GetProperty("live").GetBoolean());
        Assert.Equal(30, live.RootElement.GetProperty("plannedMinutes").GetInt32());
        Assert.Equal($"{joinCode[..3]}-{joinCode[3..]}", live.RootElement.GetProperty("joinCode").GetString());
        Assert.StartsWith($"{PodiumWebFactory.PublicOrigin}/j/", live.RootElement.GetProperty("joinUrl").GetString());
        await api.PostAsync($"/api/decks/{deck.Slug}/sessions/plan?minutes=25", null);
        Assert.Equal(25, (await ReceiveUntilAsync(presenterView, "session")).RootElement.GetProperty("plannedMinutes").GetInt32());

        // A room member admitted by the join link: gets a bare live flag (no code), and a newcomer learns it on join.
        var roomClient = app.Client();
        var (admitted, roomPage) = await PodiumWebFactory.AdmitAsync(roomClient, $"/d/{deck.Slug}/?share={linkId}");
        Assert.Equal(System.Net.HttpStatusCode.OK, roomPage.StatusCode);
        var shareCookie = admitted.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("podium_share_", StringComparison.Ordinal)).Split(';')[0];
        using var viewer = await ConnectAsync(deck.Slug, shareCookie);
        await ReceiveUntilAsync(viewer, "hello");
        var viewerSession = await ReceiveUntilAsync(viewer, "session");
        Assert.True(viewerSession.RootElement.GetProperty("live").GetBoolean());
        Assert.False(viewerSession.RootElement.TryGetProperty("joinCode", out _));

        // End: presenters get the recap, the room's sockets are closed with 4410 and the link no longer admits.
        await api.PostAsync($"/api/decks/{deck.Slug}/sessions/end?unfreeze=true", null);
        var ended = await ReceiveUntilAsync(presenterView, "session");
        Assert.False(ended.RootElement.GetProperty("live").GetBoolean());
        Assert.Equal("manual", ended.RootElement.GetProperty("reason").GetString());
        Assert.True(ended.RootElement.GetProperty("recap").GetProperty("durationSeconds").GetInt32() >= 0);

        var closed = await WaitForCloseAsync(viewer);
        Assert.Equal(4410, closed);
        Assert.Equal(WebSocketState.Open, presenterView.State);
    }

    [Fact]
    public async Task Revoking_a_share_link_disconnects_the_viewers_it_admitted()
    {
        var deck = await app.SeedDeckAsync("sync-revoke-deck");
        var api = await app.OwnerClientAsync();
        var minted = await (await api.PostAsJsonAsync($"/api/decks/{deck.Slug}/links", new { artifact = "Site" })).Content.ReadFromJsonAsync<JsonElement>();
        var linkId = minted.GetProperty("link").GetProperty("id").GetString()!;

        var guest = app.Client();
        var (admitted, _) = await PodiumWebFactory.AdmitAsync(guest, $"/d/{deck.Slug}/?share={linkId}");
        var shareCookie = admitted.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("podium_share_", StringComparison.Ordinal)).Split(';')[0];
        using var viewer = await ConnectAsync(deck.Slug, shareCookie);
        await ReceiveUntilAsync(viewer, "hello");

        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await api.PostAsync($"/api/decks/{deck.Slug}/links/{linkId}/revoke", null)).StatusCode);
        Assert.Equal(4410, await WaitForCloseAsync(viewer));
    }

    private static async Task<JsonDocument> ReceiveUntilAsync(WebSocket ws, string type, Func<JsonElement, bool> where, int maxMessages = 30)
    {
        var seen = new List<string>();
        for (var i = 0; i < maxMessages; i++)
        {
            var doc = await ReceiveAsync(ws) ?? throw new Xunit.Sdk.XunitException($"socket closed or silent while waiting for '{type}'; seen: {string.Join(" || ", seen)}");
            seen.Add(doc.RootElement.GetRawText());
            if (doc.RootElement.GetProperty("t").GetString() == type && where(doc.RootElement)) return doc;
        }
        throw new Xunit.Sdk.XunitException($"no matching '{type}' message within {maxMessages} messages; seen: {string.Join(" || ", seen)}");
    }

    private static async Task<bool> SilenceOfAsync(WebSocket ws, string type, TimeSpan window)
    {
        var until = DateTime.UtcNow + window;
        while (DateTime.UtcNow < until)
        {
            var doc = await ReceiveAsync(ws, until - DateTime.UtcNow);
            if (doc is null) return true;
            if (doc.RootElement.GetProperty("t").GetString() == type) return false;
        }
        return true;
    }

    [Fact]
    public async Task Room_reacts_asks_upvotes_and_votes_only_while_live_within_limits_and_it_all_lands_in_the_recap()
    {
        var deck = await app.SeedDeckAsync("audience-deck", Visibility.Public);
        var owner = await OwnerCookieAsync();
        var api = await app.OwnerClientAsync();
        using var presenter = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(presenter, "hello");
        await SendAsync(presenter, new { t = "hi", role = "presenter" });
        await SendAsync(presenter, new { t = "info", page = 4, total = 9, clicks = 0, clicksTotal = 0, role = "presenter" });
        using var alice = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(alice, "hello");
        await SendAsync(alice, new { t = "hi", cid = "alice-browser-0001" });

        // Nothing is live: input is dropped silently, nobody hears about it.
        await SendAsync(alice, new { t = "react", kind = "clap" });
        await SendAsync(alice, new { t = "question", text = "Is this thing on?" });
        Assert.True(await SilenceOfAsync(presenter, "reactions", TimeSpan.FromMilliseconds(1200)));

        var started = await api.PostAsJsonAsync($"/api/decks/{deck.Slug}/sessions", new { plannedMinutes = 30, holdDeploys = false, freeze = false });
        Assert.Equal(System.Net.HttpStatusCode.OK, started.StatusCode);
        var state = await ReceiveUntilAsync(alice, "audience");
        Assert.True(state.RootElement.GetProperty("live").GetBoolean());
        Assert.True(state.RootElement.GetProperty("settings").GetProperty("reactions").GetBoolean());
        Assert.True(state.RootElement.GetProperty("settings").GetProperty("floatReactions").GetBoolean());

        // Reactions are aggregated into bursts; unknown kinds are dropped.
        await SendAsync(alice, new { t = "react", kind = "clap" });
        await SendAsync(alice, new { t = "react", kind = "clap" });
        await SendAsync(alice, new { t = "react", kind = "bomb" });
        var burst = await ReceiveUntilAsync(presenter, "reactions");
        Assert.Equal(2, burst.RootElement.GetProperty("counts").GetProperty("clap").GetInt32());
        Assert.Equal(2, burst.RootElement.GetProperty("totals").GetProperty("clap").GetInt32());

        // A question (with a nick) reaches everyone, tagged with the presenter's slide; a second one inside the
        // cooldown and a too-short one are dropped.
        await SendAsync(alice, new { t = "question", text = "  How does  the laser\twork? ", nick = "Alice" });
        var questions = await ReceiveUntilAsync(presenter, "questions");
        var item = questions.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("How does the laser work?", item.GetProperty("text").GetString());
        Assert.Equal("Alice", item.GetProperty("nick").GetString());
        Assert.Equal(4, item.GetProperty("slide").GetInt32());
        Assert.Equal(1, item.GetProperty("upvotes").GetInt32());
        var questionId = item.GetProperty("id").GetString()!;
        await SendAsync(alice, new { t = "question", text = "Another one right away" });
        await SendAsync(alice, new { t = "question", text = "x" });
        Assert.True(await SilenceOfAsync(presenter, "questions", TimeSpan.FromMilliseconds(600)));

        // Another viewer upvotes (toggle), without a client id nothing counts.
        using var bob = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(bob, "hello");
        var replay = await ReceiveUntilAsync(bob, "questions");
        Assert.Equal(1, replay.RootElement.GetProperty("total").GetInt32());
        await SendAsync(bob, new { t = "upvote", id = questionId });
        Assert.True(await SilenceOfAsync(presenter, "questions", TimeSpan.FromMilliseconds(400)));
        await SendAsync(bob, new { t = "hi", cid = "bob-browser-00000001" });
        await SendAsync(bob, new { t = "upvote", id = questionId });
        Assert.Equal(2, (await ReceiveUntilAsync(presenter, "questions")).RootElement.GetProperty("items")[0].GetProperty("upvotes").GetInt32());
        await SendAsync(bob, new { t = "upvote", id = questionId });
        Assert.Equal(1, (await ReceiveUntilAsync(presenter, "questions")).RootElement.GetProperty("items")[0].GetProperty("upvotes").GetInt32());

        // Poll: the presenter creates it; viewers see options without counts until it is shown; one vote per client.
        await SendAsync(presenter, new { t = "poll", op = "create", question = "Which editor?", options = new[] { "VS Code", "Neovim", "Other" }, allowChange = true });
        var pollForPresenter = await ReceiveUntilAsync(presenter, "poll");
        var pollId = pollForPresenter.RootElement.GetProperty("poll").GetProperty("id").GetString()!;
        Assert.Equal(0, pollForPresenter.RootElement.GetProperty("poll").GetProperty("total").GetInt32());
        var pollForViewer = await ReceiveUntilAsync(alice, "poll");
        Assert.Equal(JsonValueKind.Null, pollForViewer.RootElement.GetProperty("poll").GetProperty("options")[0].GetProperty("votes").ValueKind);
        await SendAsync(alice, new { t = "vote", poll = pollId, option = 1 });
        await SendAsync(alice, new { t = "vote", poll = pollId, option = 9 });
        await SendAsync(bob, new { t = "vote", poll = pollId, option = 1 });
        await SendAsync(bob, new { t = "vote", poll = pollId, option = 0 }); // changed mind: replaces, not adds
        var tally = await ReceiveUntilAsync(presenter, "poll", p => p.GetProperty("poll").GetProperty("total").GetInt32() == 2 && p.GetProperty("poll").GetProperty("options")[0].GetProperty("votes").GetInt32() == 1);
        Assert.Equal(1, tally.RootElement.GetProperty("poll").GetProperty("options")[1].GetProperty("votes").GetInt32());
        await SendAsync(presenter, new { t = "poll", op = "show", id = pollId });
        var shown = await ReceiveUntilAsync(alice, "poll", p => p.GetProperty("poll").GetProperty("shown").GetBoolean());
        Assert.Equal(2, shown.RootElement.GetProperty("poll").GetProperty("total").GetInt32());

        // Moderation: pin, answer.
        await SendAsync(presenter, new { t = "question", op = "pin", id = questionId });
        Assert.True((await ReceiveUntilAsync(alice, "questions", q => q.GetProperty("items")[0].GetProperty("pinned").GetBoolean())).RootElement.GetProperty("items")[0].GetProperty("pinned").GetBoolean());
        await SendAsync(presenter, new { t = "question", op = "answer", id = questionId });
        Assert.True((await ReceiveUntilAsync(alice, "questions", q => q.GetProperty("items")[0].GetProperty("answered").GetBoolean())).RootElement.GetProperty("items")[0].GetProperty("answered").GetBoolean());

        // The live snapshot is available to the owner; ending writes it into the session recap.
        var snapshot = await api.GetFromJsonAsync<JsonElement>($"/api/decks/{deck.Slug}/sessions/audience");
        Assert.Equal(2, snapshot.GetProperty("reactions").GetProperty("clap").GetInt32());
        await api.PostAsync($"/api/decks/{deck.Slug}/sessions/end?unfreeze=true", null);
        var ended = await ReceiveUntilAsync(presenter, "session", s => !s.GetProperty("live").GetBoolean());
        Assert.Equal(2, ended.RootElement.GetProperty("audience").GetProperty("reactions").GetInt32());
        Assert.Equal(1, ended.RootElement.GetProperty("audience").GetProperty("questions").GetInt32());
        Assert.Equal(1, ended.RootElement.GetProperty("audience").GetProperty("polls").GetInt32());
        var list = await api.GetFromJsonAsync<JsonElement>($"/api/decks/{deck.Slug}/sessions");
        var recap = list.EnumerateArray().First().GetProperty("audienceRecap");
        Assert.Equal(2, recap.GetProperty("reactions").GetProperty("clap").GetInt32());
        Assert.Equal("How does the laser work?", recap.GetProperty("questions")[0].GetProperty("text").GetString());
        Assert.True(recap.GetProperty("questions")[0].GetProperty("answered").GetBoolean());
        Assert.Equal(2, recap.GetProperty("polls")[0].GetProperty("totalVotes").GetInt32());
        // After the end the room is deaf again.
        Assert.False((await ReceiveUntilAsync(alice, "audience", a => !a.GetProperty("live").GetBoolean())).RootElement.GetProperty("live").GetBoolean());
    }

    [Fact]
    public async Task Audience_features_follow_the_deck_settings_and_the_room_can_be_muted()
    {
        var deck = await app.SeedDeckAsync("audience-off-deck", Visibility.Public);
        var owner = await OwnerCookieAsync();
        var api = await app.OwnerClientAsync();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await api.PatchAsync($"/api/decks/{deck.Slug}", JsonContent.Create(new { audience = new { reactions = false, questions = true, polls = true, floatReactions = false, nicknames = false } }))).StatusCode);
        using var presenter = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(presenter, "hello");
        using var viewer = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(viewer, "hello");
        await SendAsync(viewer, new { t = "hi", cid = "viewer-browser-0001" });
        await api.PostAsJsonAsync($"/api/decks/{deck.Slug}/sessions", new { plannedMinutes = 10, holdDeploys = false, freeze = false });
        var state = await ReceiveUntilAsync(viewer, "audience");
        Assert.False(state.RootElement.GetProperty("settings").GetProperty("reactions").GetBoolean());
        Assert.False(state.RootElement.GetProperty("settings").GetProperty("nicknames").GetBoolean());

        await SendAsync(viewer, new { t = "react", kind = "clap" });
        Assert.True(await SilenceOfAsync(presenter, "reactions", TimeSpan.FromMilliseconds(1200)));
        // Nicknames are off: the question arrives anonymous.
        await SendAsync(viewer, new { t = "question", text = "Anonymous enough?", nick = "Eve" });
        var q = await ReceiveUntilAsync(presenter, "questions");
        Assert.Equal(JsonValueKind.Null, q.RootElement.GetProperty("items")[0].GetProperty("nick").ValueKind);

        // Muting the room drops everything until unmuted; the room is told.
        Assert.Equal(System.Net.HttpStatusCode.OK, (await api.PostAsJsonAsync($"/api/decks/{deck.Slug}/sessions/audience", new { muted = true })).StatusCode);
        Assert.True((await ReceiveUntilAsync(viewer, "audience", a => a.GetProperty("muted").GetBoolean())).RootElement.GetProperty("muted").GetBoolean());
        using var other = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(other, "hello");
        await SendAsync(other, new { t = "hi", cid = "other-browser-0001" });
        await SendAsync(other, new { t = "question", text = "Can anyone hear me?" });
        Assert.True(await SilenceOfAsync(presenter, "questions", TimeSpan.FromMilliseconds(600)));
        await api.PostAsJsonAsync($"/api/decks/{deck.Slug}/sessions/audience", new { muted = false, settings = new { reactions = true, questions = true, polls = true, floatReactions = true, nicknames = true } });
        await ReceiveUntilAsync(viewer, "audience", a => !a.GetProperty("muted").GetBoolean() && a.GetProperty("settings").GetProperty("reactions").GetBoolean());
        await SendAsync(other, new { t = "react", kind = "heart" });
        Assert.Equal(1, (await ReceiveUntilAsync(presenter, "reactions")).RootElement.GetProperty("counts").GetProperty("heart").GetInt32());
        await api.PostAsync($"/api/decks/{deck.Slug}/sessions/end?unfreeze=true", null);
    }

    [Fact]
    public async Task Polls_can_lock_answers_and_presenters_keep_a_history_they_can_re_show()
    {
        var deck = await app.SeedDeckAsync("audience-polls-deck", Visibility.Public);
        var owner = await OwnerCookieAsync();
        var api = await app.OwnerClientAsync();
        using var presenter = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(presenter, "hello");
        await SendAsync(presenter, new { t = "hi", role = "presenter" });
        using var ann = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(ann, "hello");
        await SendAsync(ann, new { t = "hi", cid = "ann-browser-00000001" });
        await api.PostAsJsonAsync($"/api/decks/{deck.Slug}/sessions", new { plannedMinutes = 30, holdDeploys = false, freeze = false });
        await ReceiveUntilAsync(ann, "audience");

        // Final answers (default): the first vote counts, a second one is dropped.
        await SendAsync(presenter, new { t = "poll", op = "create", question = "Coffee or tea?", options = new[] { "Coffee", "Tea" } });
        var first = await ReceiveUntilAsync(presenter, "poll");
        var firstId = first.RootElement.GetProperty("poll").GetProperty("id").GetString()!;
        Assert.False(first.RootElement.GetProperty("poll").GetProperty("allowChange").GetBoolean());
        var history = await ReceiveUntilAsync(presenter, "polls");
        Assert.Single(history.RootElement.GetProperty("items").EnumerateArray());
        await SendAsync(ann, new { t = "vote", poll = firstId, option = 0 });
        await ReceiveUntilAsync(presenter, "poll", p => p.GetProperty("poll").GetProperty("total").GetInt32() == 1);
        await SendAsync(ann, new { t = "vote", poll = firstId, option = 1 });
        Assert.True(await SilenceOfAsync(presenter, "poll", TimeSpan.FromMilliseconds(600)));
        var snapshot = await api.GetFromJsonAsync<JsonElement>($"/api/decks/{deck.Slug}/sessions/audience");
        Assert.Equal(1, snapshot.GetProperty("polls")[0].GetProperty("options")[0].GetProperty("votes").GetInt32());
        Assert.Equal(0, snapshot.GetProperty("polls")[0].GetProperty("options")[1].GetProperty("votes").GetInt32());

        // A second poll with changeable answers: the earlier one is closed and leaves the stage, the history keeps both.
        await SendAsync(presenter, new { t = "poll", op = "create", question = "Which editor?", options = new[] { "VS Code", "Neovim" }, allowChange = true });
        var second = await ReceiveUntilAsync(presenter, "poll", p => p.GetProperty("poll").GetProperty("question").GetString() == "Which editor?");
        var secondId = second.RootElement.GetProperty("poll").GetProperty("id").GetString()!;
        Assert.True(second.RootElement.GetProperty("poll").GetProperty("allowChange").GetBoolean());
        var both = await ReceiveUntilAsync(presenter, "polls", p => p.GetProperty("items").GetArrayLength() == 2);
        Assert.Equal("Which editor?", both.RootElement.GetProperty("items")[0].GetProperty("question").GetString()); // newest first
        Assert.False(both.RootElement.GetProperty("items")[1].GetProperty("open").GetBoolean());
        await SendAsync(ann, new { t = "vote", poll = secondId, option = 0 });
        await ReceiveUntilAsync(presenter, "poll", p => p.GetProperty("poll").GetProperty("options")[0].GetProperty("votes").GetInt32() == 1);
        await SendAsync(ann, new { t = "vote", poll = secondId, option = 1 });
        var changed = await ReceiveUntilAsync(presenter, "poll", p => p.GetProperty("poll").GetProperty("options")[1].GetProperty("votes").GetInt32() == 1);
        Assert.Equal(0, changed.RootElement.GetProperty("poll").GetProperty("options")[0].GetProperty("votes").GetInt32());
        Assert.Equal(1, changed.RootElement.GetProperty("poll").GetProperty("total").GetInt32());

        // Re-showing the earlier poll puts it back in front of the room (and closes the one that was open).
        await SendAsync(presenter, new { t = "poll", op = "show", id = firstId });
        var reshown = await ReceiveUntilAsync(ann, "poll", p => p.GetProperty("poll").GetProperty("id").GetString() == firstId && p.GetProperty("poll").GetProperty("shown").GetBoolean(), 60);
        Assert.True(reshown.RootElement.GetProperty("poll").GetProperty("shown").GetBoolean());
        Assert.Equal(1, reshown.RootElement.GetProperty("poll").GetProperty("total").GetInt32()); // shown: counts revealed to viewers
        var afterReshow = await ReceiveUntilAsync(presenter, "polls", p => p.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetString() == firstId && i.GetProperty("shown").GetBoolean()));
        Assert.All(afterReshow.RootElement.GetProperty("items").EnumerateArray(), i => Assert.False(i.GetProperty("open").GetBoolean()));

        // A presenter joining later gets the history replayed; the recap remembers the answer policy.
        using var lateRemote = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(lateRemote, "hello");
        var replayed = await ReceiveUntilAsync(lateRemote, "polls");
        Assert.Equal(2, replayed.RootElement.GetProperty("items").GetArrayLength());
        await api.PostAsync($"/api/decks/{deck.Slug}/sessions/end?unfreeze=true", null);
        var list = await api.GetFromJsonAsync<JsonElement>($"/api/decks/{deck.Slug}/sessions");
        var polls = list.EnumerateArray().First().GetProperty("audienceRecap").GetProperty("polls");
        Assert.False(polls[0].GetProperty("allowChange").GetBoolean());
        Assert.True(polls[1].GetProperty("allowChange").GetBoolean());
    }

    private static async Task<int> WaitForCloseAsync(WebSocket ws)
    {
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, cts.Token);
            if (r.MessageType != WebSocketMessageType.Close) continue;
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token); } catch (WebSocketException) { }
            return (int)(r.CloseStatus ?? 0);
        }
    }

    [Fact]
    public async Task Cross_origin_and_private_deck_connections_are_refused()
    {
        var deck = await app.SeedDeckAsync("sync-private-deck");
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(deck.Slug, null));
        var pub = await app.SeedDeckAsync("sync-origin-deck", Visibility.Public);
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(pub.Slug, null, origin: "https://evil.example"));
    }
}
