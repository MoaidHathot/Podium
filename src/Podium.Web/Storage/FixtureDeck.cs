namespace Podium.Web.Storage;

/// <summary>
/// A minimal "deck" used by the dev-seed endpoint: a three-page PDF-kind deck in the shape the builder produces
/// for PowerPoint/PDF decks (pages viewer shell + page images), so CI exercises the real server-served runtime:
/// bridge.js, pages.js, live-ui.js, the relay, the remote and live.js in a browser, without building anything.
/// </summary>
public static class FixtureDeck
{
    public const string BuildId = "fixture1";
    public const int Pages = 3;

    public static string IndexHtml(string slug) => $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
        <meta name="podium-build" content="{{BuildId}}"><meta name="podium-slug" content="{{slug}}"><meta name="podium-addon" content="3">
        <title>Fixture deck</title>
        <style>html,body{margin:0;height:100%;background:#000;color:#e6e9f0;font-family:system-ui,sans-serif}main.podium-pages{position:fixed;inset:0;outline:0}a{color:#7c9cff}</style>
        </head><body>
        <main id="podium-pages" class="podium-pages" data-pages="{&quot;count&quot;:{{Pages}},&quot;aspect&quot;:1.7778}" aria-label="Fixture deck" tabindex="0"></main>
        <noscript><p style="padding:2rem">This presentation needs JavaScript.</p></noscript>
        </body></html>
        """;

    /// <summary>A valid 1x1 JPEG; the viewer only needs something that decodes.</summary>
    public static readonly byte[] PageJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=");

    public static string PagesJson => $$"""{"count":{{Pages}},"aspect":1.7778}""";
}
