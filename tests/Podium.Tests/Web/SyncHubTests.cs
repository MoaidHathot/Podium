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
        var admitted = await roomClient.SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/?share={linkId}"));
        Assert.Equal(System.Net.HttpStatusCode.OK, admitted.StatusCode);
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
        var admitted = await guest.SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/?share={linkId}"));
        var shareCookie = admitted.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("podium_share_", StringComparison.Ordinal)).Split(';')[0];
        using var viewer = await ConnectAsync(deck.Slug, shareCookie);
        await ReceiveUntilAsync(viewer, "hello");

        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await api.PostAsync($"/api/decks/{deck.Slug}/links/{linkId}/revoke", null)).StatusCode);
        Assert.Equal(4410, await WaitForCloseAsync(viewer));
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
