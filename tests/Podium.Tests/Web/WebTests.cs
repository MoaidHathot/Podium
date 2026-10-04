using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Tests.Web;

[Collection(nameof(WebCollection))]
public sealed class AuthAndApiTests(PodiumWebFactory app)
{
    [Fact]
    public async Task Anonymous_ui_redirects_to_login_and_api_returns_401()
    {
        var c = app.Client();
        var ui = await c.SendAsync(PodiumWebFactory.Navigation("/"));
        Assert.Equal(HttpStatusCode.Redirect, ui.StatusCode);
        Assert.StartsWith("/login", PathOf(ui.Headers.Location!));
        var api = await c.GetAsync("/api/decks");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }

    [Fact]
    public async Task Signed_in_non_owner_is_denied_everywhere_the_owner_goes()
    {
        var guest = await app.GuestClientAsync();
        var ui = await guest.SendAsync(PodiumWebFactory.Navigation("/"));
        Assert.Equal(HttpStatusCode.Redirect, ui.StatusCode);
        Assert.StartsWith("/denied", PathOf(ui.Headers.Location!));
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/decks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsync("/api/decks/anything/rebuild", null)).StatusCode);
    }

    [Fact]
    public async Task Owner_api_mutations_require_the_request_header()
    {
        var deck = await app.SeedDeckAsync("csrf-deck");
        var owner = await app.OwnerClientAsync();
        owner.DefaultRequestHeaders.Remove("X-Podium-Request");
        var noHeader = await owner.PatchAsync($"/api/decks/{deck.Slug}", JsonContent.Create(new { pinned = true }));
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/decks/{deck.Slug}")).StatusCode); // GETs are exempt

        owner.DefaultRequestHeaders.Add("X-Podium-Request", "1");
        var ok = await owner.PatchAsync($"/api/decks/{deck.Slug}", JsonContent.Create(new { pinned = true }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True((await app.Services.GetRequiredService<IDeckStore>().GetAsync(deck.Slug))!.Pinned);
    }

    [Fact]
    public async Task Alias_patch_rejects_clashes_with_slugs_and_other_aliases()
    {
        await app.SeedDeckAsync("alias-a", alias: "short-a");
        var b = await app.SeedDeckAsync("alias-b");
        var owner = await app.OwnerClientAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PatchAsync($"/api/decks/{b.Slug}", JsonContent.Create(new { alias = "short-a" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PatchAsync($"/api/decks/{b.Slug}", JsonContent.Create(new { alias = "alias-a" }))).StatusCode);
        var ok = await owner.PatchAsync($"/api/decks/{b.Slug}", JsonContent.Create(new { alias = "Short B!" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("short-b", (await app.Services.GetRequiredService<IDeckStore>().GetAsync(b.Slug))!.Alias);
    }

    [Fact]
    public async Task Only_archived_decks_can_be_deleted_and_deletion_purges_everything()
    {
        var live = await app.SeedDeckAsync("delete-live");
        var gone = await app.SeedDeckAsync("delete-archived", archived: true);
        var owner = await app.OwnerClientAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/decks/{live.Slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/decks/{gone.Slug}")).StatusCode);
        Assert.Null(await app.Services.GetRequiredService<IDeckStore>().GetAsync(gone.Slug));
        Assert.Contains(app.Artifacts.Deleted, d => d.Slug == gone.Slug);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/decks/{gone.Slug}")).StatusCode);
    }

    [Fact]
    public async Task Webhook_rejects_missing_and_wrong_signatures()
    {
        var c = app.Client();
        var body = """{"ref":"refs/heads/main","repository":{"full_name":"owner/slides"}}""";
        var unsigned = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        unsigned.Headers.Add("X-GitHub-Event", "push");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(unsigned)).StatusCode);

        var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        wrong.Headers.Add("X-GitHub-Event", "push");
        wrong.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("not-the-secret"), Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(wrong)).StatusCode);
    }

    [Fact]
    public async Task Push_from_a_renamed_repository_is_matched_by_github_id_and_relabels_the_source()
    {
        var sources = app.Services.GetRequiredService<ISourceStore>();
        await sources.UpsertAsync(new Source { Id = "owner/old-name", Owner = "owner", Repo = "old-name", RepoId = 987654, Trusted = true, LastSeenSha = "0123456789abcdef0123456789abcdef01234567" });
        var body = """{"ref":"refs/heads/main","forced":false,"commits":[],"repository":{"id":987654,"name":"new-name","full_name":"owner/new-name","default_branch":"main","owner":{"login":"owner"}}}""";
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-GitHub-Event", "push");
        req.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        req.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("whsec-test"), Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
        var r = await app.Client().SendAsync(req);
        Assert.True(r.IsSuccessStatusCode, $"webhook returned {(int)r.StatusCode}");

        // The delivery is acknowledged first and processed right after; give the background step a moment.
        Source? relabelled = null;
        for (var i = 0; i < 100 && relabelled?.Repo != "new-name"; i++) { await Task.Delay(50); relabelled = await sources.GetAsync("owner/old-name"); }
        Assert.NotNull(relabelled);
        Assert.Equal("new-name", relabelled!.Repo);
        Assert.Equal("owner/new-name", relabelled.FullName);
        Assert.Null(await sources.GetAsync("owner/new-name")); // no duplicate source, decks keep their slugs
        // The owner API addresses it by its current name.
        var owner = await app.OwnerClientAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/sources/owner/new-name")).StatusCode);
        Assert.Null(await sources.GetAsync("owner/old-name"));
    }

    [Fact]
    public async Task Rerun_of_a_podium_check_run_rebuilds_the_deck_and_ignores_everything_else()
    {
        var sources = app.Services.GetRequiredService<ISourceStore>();
        var decks = app.Services.GetRequiredService<IDeckStore>();
        await sources.UpsertAsync(new Source { Id = "owner/checks-repo", Owner = "owner", Repo = "checks-repo", RepoId = 555001, Trusted = true, LastSeenSha = "1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        await decks.UpsertAsync(new Deck { Slug = "checks-deck", SourceId = "owner/checks-repo", Path = "talk", Entry = "slides.md", Kind = DeckKind.Slidev, Title = "Checks", LastCommitSha = "2222222aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        var externalId = Podium.Web.GitHub.GitHubChecksObserver.ExternalId(new Deck { Slug = "checks-deck", SourceId = "x", Path = "", Entry = "", Kind = DeckKind.Slidev }, new Build { Id = "b0001", DeckSlug = "checks-deck", Sha = "x" });
        Assert.Equal("checks-deck/b0001", externalId);
        var before = app.Runner.Started.Count;

        // Echoes of our own runs and foreign check runs do nothing.
        Assert.True((await Deliver("check_run", CheckRunPayload("created", externalId, 555001))).IsSuccessStatusCode);
        Assert.True((await Deliver("check_run", CheckRunPayload("rerequested", "some-other-app-id", 555001))).IsSuccessStatusCode);
        Assert.True((await Deliver("check_run", CheckRunPayload("rerequested", externalId, 999999))).IsSuccessStatusCode); // wrong repository
        await Task.Delay(500);
        Assert.Equal(before, app.Runner.Started.Count);

        // A genuine re-run queues a build at the deck's current commit.
        Assert.True((await Deliver("check_run", CheckRunPayload("rerequested", externalId, 555001))).IsSuccessStatusCode);
        BuildRequest? started = null;
        for (var i = 0; i < 100 && started is null; i++) { await Task.Delay(50); started = app.Runner.Started.FirstOrDefault(r => r.Deck.Slug == "checks-deck"); }
        Assert.NotNull(started);
        Assert.Equal("2222222aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", started!.Build.Sha);
        Assert.Equal("check-rerun", started.Build.TriggeredBy);
        Assert.Equal(BuildStatus.Running, (await decks.GetAsync("checks-deck"))!.LatestBuildStatus);
    }

    private async Task<HttpResponseMessage> Deliver(string eventName, string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-GitHub-Event", eventName);
        req.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        req.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("whsec-test"), Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
        return await app.Client().SendAsync(req);
    }

    private static string CheckRunPayload(string action, string externalId, long repoId) => JsonSerializer.Serialize(new
    {
        action,
        check_run = new { id = 77, external_id = externalId, head_sha = "2222222aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", name = "Podium / Checks", app = new { id = 0 } },
        repository = new { id = repoId, name = "checks-repo", full_name = "owner/checks-repo", owner = new { login = "owner" } },
        installation = new { id = 1 },
    });

    [Fact]
    public async Task Builder_report_requires_a_valid_callback_token()
    {
        var deck = await app.SeedDeckAsync("report-deck");
        var c = app.Client();
        var r = await c.PostAsync($"/api/builds/{deck.Slug}/{deck.CurrentBuildId}/report", JsonContent.Create(new { success = true, hasSite = true }));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var bad = new HttpRequestMessage(HttpMethod.Post, $"/api/builds/{deck.Slug}/{deck.CurrentBuildId}/report") { Content = JsonContent.Create(new { success = true }) };
        bad.Headers.Authorization = new("Bearer", "forged");
        Assert.True((await c.SendAsync(bad)).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Ui_pages_carry_a_nonce_based_csp_and_deck_pages_do_not()
    {
        var owner = await app.OwnerClientAsync();
        var library = await owner.SendAsync(PodiumWebFactory.Navigation("/"));
        Assert.Equal(HttpStatusCode.OK, library.StatusCode);
        var csp = library.Headers.GetValues("Content-Security-Policy").Single();
        var nonce = System.Text.RegularExpressions.Regex.Match(csp, "'nonce-([^']+)'").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(nonce));
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        var html = await library.Content.ReadAsStringAsync();
        // Every inline script on the page carries this response's nonce; none is left bare.
        var inline = System.Text.RegularExpressions.Regex.Matches(html, "<script(?![^>]*\\ssrc=)[^>]*>");
        Assert.NotEmpty(inline);
        Assert.All(inline, m => Assert.Contains($"nonce=\"{nonce}\"", m.Value));
        // A second response gets a different nonce.
        var again = await owner.SendAsync(PodiumWebFactory.Navigation("/"));
        Assert.NotEqual(csp, again.Headers.GetValues("Content-Security-Policy").Single());

        var deck = await app.SeedDeckAsync("csp-free-deck", Visibility.Public);
        var served = await app.Client().SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/"));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.False(served.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("SAMEORIGIN", served.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Inline_scripts_on_every_owner_page_parse_as_javascript()
    {
        // Razor pages carry hand-written scripts; a mangled template literal silently disables a whole page's
        // controls while the API keeps working (it happened). Parse what the server actually renders.
        var deck = await app.SeedDeckAsync("js-check-deck", Visibility.Public, alias: "jscheck");
        var owner = await app.OwnerClientAsync();
        await owner.PostAsync($"/api/decks/{deck.Slug}/links", JsonContent.Create(new { artifact = "Site", label = "l", expiresInDays = 7 }));
        var parser = new Acornima.Parser(new Acornima.ParserOptions { Tolerant = false });
        foreach (var path in new[] { "/", "/sources", $"/decks/{deck.Slug}", "/login" })
        {
            var r = await owner.SendAsync(PodiumWebFactory.Navigation(path));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var html = await r.Content.ReadAsStringAsync();
            var scripts = System.Text.RegularExpressions.Regex.Matches(html, "<script(?![^>]*\\ssrc=)[^>]*>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
            foreach (System.Text.RegularExpressions.Match m in scripts)
            {
                try { parser.ParseScript(m.Groups[1].Value); }
                catch (Acornima.ParseErrorException ex) { Assert.Fail($"{path}: inline script does not parse: {ex.Message}"); }
            }
        }
    }

    internal static string PathOf(Uri location) => location.IsAbsoluteUri ? location.PathAndQuery : location.ToString();
}

[Collection(nameof(WebCollection))]
public sealed class ServingTests(PodiumWebFactory app)
{
    [Fact]
    public async Task Private_deck_is_invisible_to_anonymous_and_guests_but_served_to_the_owner()
    {
        var deck = await app.SeedDeckAsync("private-deck");
        var anon = await app.Client().SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/"));
        Assert.Equal(HttpStatusCode.Redirect, anon.StatusCode);
        Assert.StartsWith("/login", AuthAndApiTests.PathOf(anon.Headers.Location!));
        var guest = await (await app.GuestClientAsync()).SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/"));
        Assert.True(guest.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Redirect);
        if (guest.StatusCode == HttpStatusCode.Redirect) Assert.DoesNotContain(deck.Slug, guest.Headers.Location!.ToString());

        var owner = await (await app.OwnerClientAsync()).SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/"));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        var html = await owner.Content.ReadAsStringAsync();
        Assert.Contains($"<h1>{deck.Slug}</h1>", html);
        Assert.Contains("/_podium/live.js", html);
        Assert.DoesNotContain("og:url", html); // no link-preview metadata for private decks
        Assert.Equal(deck.CurrentBuildId, owner.Headers.GetValues("X-Podium-Build").Single());
    }

    [Fact]
    public async Task Public_deck_serves_anonymously_with_open_graph_tags_and_thumbnail()
    {
        var deck = await app.SeedDeckAsync("public-deck", Visibility.Public);
        var c = app.Client();
        var r = await c.SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains($"<meta property=\"og:url\" content=\"{PodiumWebFactory.PublicOrigin}/d/{deck.Slug}/\">", html);
        Assert.Contains($"<meta property=\"og:image\" content=\"{PodiumWebFactory.PublicOrigin}/d/{deck.Slug}.jpg?v={deck.CurrentBuildId}\">", html);
        Assert.Equal(1, CountOf(html, "property=\"og:title\""));
        var thumb = await c.GetAsync($"/d/{deck.Slug}.jpg");
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal("image/jpeg", thumb.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Decks_that_already_declare_open_graph_tags_are_not_duplicated()
    {
        var own = "<!doctype html><html><head><meta property=\"og:title\" content=\"Mine\"><meta property=\"og:description\" content=\"Own\"></head><body>x</body></html>";
        var deck = await app.SeedDeckAsync("own-og-deck", Visibility.Public, html: own);
        var html = await (await app.Client().SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/"))).Content.ReadAsStringAsync();
        Assert.Equal(1, CountOf(html, "property=\"og:title\""));
        Assert.Contains("content=\"Mine\"", html);
        Assert.Equal(1, CountOf(html, "property=\"og:url\""));
    }

    [Fact]
    public async Task Alias_redirects_to_the_canonical_slug_keeping_path_and_query()
    {
        var deck = await app.SeedDeckAsync("alias-target", Visibility.Public, alias: "tgt");
        var c = app.Client();
        var root = await c.SendAsync(PodiumWebFactory.Navigation("/d/tgt/"));
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal($"/d/{deck.Slug}/", root.Headers.Location!.ToString());
        var deep = await c.SendAsync(PodiumWebFactory.Navigation("/d/tgt/7?clicks=2"));
        Assert.Equal(HttpStatusCode.Redirect, deep.StatusCode);
        Assert.Equal($"/d/{deck.Slug}/7?clicks=2", deep.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Unknown_deck_is_404_for_the_owner_and_hidden_from_anonymous()
    {
        var owner = await app.OwnerClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.SendAsync(PodiumWebFactory.Navigation("/d/does-not-exist/"))).StatusCode);
        var anon = await app.Client().SendAsync(PodiumWebFactory.Navigation("/d/does-not-exist/"));
        Assert.True(anon.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Share_link_grants_anonymous_access_and_sets_a_host_wide_cookie()
    {
        var deck = await app.SeedDeckAsync("linked-deck");
        var owner = await app.OwnerClientAsync();
        var created = await owner.PostAsync($"/api/decks/{deck.Slug}/links", JsonContent.Create(new { artifact = "Site", label = "test", expiresInDays = 1 }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("link").GetProperty("id").GetString();

        var viewer = app.Client();
        var first = await viewer.SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/?share={id}"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var cookie = first.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("podium_share_", StringComparison.Ordinal));
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        // The cookie alone (no ?share=) keeps working, including for the version probe outside /d/{slug}/.
        Assert.Equal(HttpStatusCode.OK, (await viewer.SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/decks/{deck.Slug}/version")).StatusCode);

        // Revoking shuts the door for new visitors.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/decks/{deck.Slug}/links/{id}/revoke", null)).StatusCode);
        var after = await app.Client().SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/?share={id}"));
        Assert.NotEqual(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Pdf_export_follows_its_own_visibility()
    {
        var deck = await app.SeedDeckAsync("pdf-deck", Visibility.Public);
        app.Artifacts.PutArtifact(deck.Slug, deck.CurrentBuildId!, ArtifactKind.Pdf, "application/pdf", "%PDF-1.4 test"u8.ToArray());
        await app.Services.GetRequiredService<IDeckStore>().UpsertAsync(deck with { CurrentHasPdf = true, PdfVisibility = Visibility.Private });
        app.Services.GetRequiredService<Podium.Web.Serving.DeckAccessService>().Invalidate(deck.Slug);
        var anon = await app.Client().GetAsync($"/d/{deck.Slug}.pdf");
        Assert.NotEqual(HttpStatusCode.OK, anon.StatusCode);
        var owner = await (await app.OwnerClientAsync()).GetAsync($"/d/{deck.Slug}.pdf");
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Equal("application/pdf", owner.Content.Headers.ContentType!.MediaType);
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}

[Collection(nameof(WebCollection))]
public sealed class ExternalHostTests(PodiumWebFactory app)
{
    [Fact]
    public async Task Untrusted_deck_is_never_served_from_the_primary_origin()
    {
        var deck = await app.SeedDeckAsync("stranger-deck", Visibility.Public, trusted: false);
        var c = app.Client();
        var r = await c.SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var target = r.Headers.Location!;
        Assert.Equal(new Uri(PodiumWebFactory.ExternalOrigin).Host, target.Host);
        Assert.Contains("podium_vt=", target.Query);
        // Sub-resource requests (no navigation) get nothing rather than a redirect a script could follow.
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/d/{deck.Slug}/assets/app.js")).StatusCode);
    }

    [Fact]
    public async Task External_origin_exchanges_the_view_token_for_a_cookie_and_serves_the_deck()
    {
        var deck = await app.SeedDeckAsync("stranger-deck-2", Visibility.Public, trusted: false);
        var primary = app.Client();
        var hop = await primary.SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/"));
        var target = hop.Headers.Location!;

        var external = app.Client(PodiumWebFactory.ExternalOrigin);
        var exchange = await external.SendAsync(PodiumWebFactory.Navigation(target.PathAndQuery));
        Assert.Equal(HttpStatusCode.Redirect, exchange.StatusCode);
        Assert.Equal($"/d/{deck.Slug}/", exchange.Headers.Location!.ToString());
        var cookie = exchange.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("podium_view=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        var served = await external.SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/"));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Contains($"<h1>{deck.Slug}</h1>", await served.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task External_origin_rejects_forged_tokens_and_bounces_ui_paths_home()
    {
        var external = app.Client(PodiumWebFactory.ExternalOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, (await external.SendAsync(PodiumWebFactory.Navigation("/d/anything/?podium_vt=forged.token"))).StatusCode);
        var ui = await external.SendAsync(PodiumWebFactory.Navigation("/sources"));
        Assert.Equal(HttpStatusCode.Redirect, ui.StatusCode);
        Assert.StartsWith(PodiumWebFactory.PublicOrigin, ui.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Owner_session_cookie_carries_no_authority_on_the_external_origin()
    {
        // Deck code running on the external origin could at most replay the owner's cookie there; it must buy nothing.
        var deck = await app.SeedDeckAsync("private-on-external");
        var raw = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri(PodiumWebFactory.PublicOrigin) });
        var login = await raw.GetAsync("/dev-login?returnUrl=/");
        var session = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("podium_session=", StringComparison.Ordinal)).Split(';')[0];

        var onPrimary = PodiumWebFactory.Navigation($"/d/{deck.Slug}/"); onPrimary.Headers.Add("Cookie", session);
        Assert.Equal(HttpStatusCode.OK, (await raw.SendAsync(onPrimary)).StatusCode);

        var external = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri(PodiumWebFactory.ExternalOrigin) });
        var onExternal = PodiumWebFactory.Navigation($"/d/{deck.Slug}/"); onExternal.Headers.Add("Cookie", session);
        var cross = await external.SendAsync(onExternal);
        Assert.NotEqual(HttpStatusCode.OK, cross.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, cross.StatusCode);
        Assert.StartsWith(PodiumWebFactory.PublicOrigin, cross.Headers.Location!.ToString());
    }
}

[Collection(nameof(WebCollection))]
public sealed class RateLimitTests(PodiumWebFactory app)
{
    [Fact]
    public async Task Deck_entry_points_are_limited_per_client_ip()
    {
        var deck = await app.SeedDeckAsync("limited-deck", Visibility.Public);
        var c = app.Client(forwardedFor: "203.0.113.77");
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 95 && last != HttpStatusCode.TooManyRequests; i++)
            last = (await c.GetAsync($"/d/{deck.Slug}.pdf")).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        // Another client is unaffected.
        var other = await app.Client(forwardedFor: "203.0.113.78").SendAsync(PodiumWebFactory.Navigation($"/d/{deck.Slug}/"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Login_is_limited_per_client_ip()
    {
        var c = app.Client(forwardedFor: "203.0.113.90");
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 25 && last != HttpStatusCode.TooManyRequests; i++)
            last = (await c.GetAsync("/login/github")).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}

[CollectionDefinition(nameof(WebCollection))]
public sealed class WebCollection : ICollectionFixture<PodiumWebFactory>;
