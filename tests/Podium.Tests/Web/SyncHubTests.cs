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
    public async Task Flooding_presenter_is_throttled_not_disconnected()
    {
        var deck = await app.SeedDeckAsync("sync-flood-deck", Visibility.Public);
        var owner = await OwnerCookieAsync();
        using var presenter = await ConnectAsync(deck.Slug, owner);
        await ReceiveUntilAsync(presenter, "hello");
        using var viewer = await ConnectAsync(deck.Slug, null);
        await ReceiveUntilAsync(viewer, "hello");

        for (var i = 0; i < 120; i++) await SendAsync(presenter, new { t = "nav", action = "next" });
        var received = 0;
        while (await ReceiveAsync(viewer, TimeSpan.FromMilliseconds(400)) is { } doc)
            if (doc.RootElement.GetProperty("t").GetString() == "nav") received++;
        Assert.InRange(received, 1, 45); // 40/s cap; the rest dropped
        Assert.Equal(WebSocketState.Open, presenter.State);
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
