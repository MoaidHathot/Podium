using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Web.Security;
using Podium.Web.Serving;

namespace Podium.Tests.Web;

public sealed class MarkdownTests
{
    [Fact]
    public void Author_html_never_becomes_markup()
    {
        var html = Markdown.ToHtml("Hello <script>alert(1)</script> & <img src=x onerror=alert(1)>\n\n<b>bold?</b>");
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("&amp;", html);
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](data:text/html,hi)")]
    [InlineData("[x](vbscript:msgbox)")]
    [InlineData("[x](//evil.test/relative)")]
    public void Links_with_unsafe_schemes_are_dropped_to_text(string md)
    {
        var html = Markdown.ToHtml(md);
        Assert.DoesNotContain("href", html);
        Assert.Contains("<p>x</p>", html);
    }

    [Fact]
    public void Links_and_bare_urls_open_safely()
    {
        var html = Markdown.ToHtml("See [docs](https://example.test/a?b=1&c=2) and https://example.test/x.");
        Assert.Contains("<a href=\"https://example.test/a?b=1&amp;c=2\" rel=\"noopener noreferrer\" target=\"_blank\">docs</a>", html);
        Assert.Contains("<a href=\"https://example.test/x\" rel=\"noopener noreferrer\" target=\"_blank\">https://example.test/x</a>.", html);
    }

    [Fact]
    public void Emphasis_cannot_reach_into_generated_attributes()
    {
        // A URL carrying ** and a later ** must not wrap part of the href in <strong>.
        var html = Markdown.ToHtml("[a](https://x.test/**) and **b**");
        Assert.Contains("href=\"https://x.test/**\"", html);
        Assert.Contains("<strong>b</strong>", html);
        Assert.DoesNotContain("<a href=\"https://x.test/<strong>", html);
    }

    [Fact]
    public void Code_spans_keep_their_content_literal()
    {
        var html = Markdown.ToHtml("Use `<T>` and `**not bold**` here.");
        Assert.Contains("<code>&lt;T&gt;</code>", html);
        Assert.Contains("<code>**not bold**</code>", html);
        Assert.DoesNotContain("<strong>", html);
    }

    [Fact]
    public void Blocks_render_as_expected()
    {
        var html = Markdown.ToHtml("# Top\n\nPara one\ncontinues.\n\n- a\n- **b**\n\n1. one\n2. two\n\n> quoted\n\n```\nlet x = \"<y>\";\n```\n\n---\n");
        Assert.Contains("<h3>Top</h3>", html); // headings never compete with the page's own h1/h2
        Assert.Contains("<p>Para one continues.</p>", html);
        Assert.Contains("<ul>\n<li>a</li>\n<li><strong>b</strong></li>\n</ul>", html);
        Assert.Contains("<ol>\n<li>one</li>\n<li>two</li>\n</ol>", html);
        Assert.Contains("<blockquote><p>quoted</p>\n</blockquote>", html);
        Assert.Contains("<pre><code>let x = &quot;&lt;y&gt;&quot;;</code></pre>", html);
        Assert.Contains("<hr>", html);
    }

    [Fact]
    public void Control_characters_cannot_forge_slots()
    {
        var html = Markdown.ToHtml("\u00010\u0001 `x`");
        Assert.Equal("<p>0 <code>x</code></p>\n", html);
    }

    [Fact]
    public void ToText_strips_markup()
    {
        Assert.Equal("Title\nBold and em with code and a link.\n- item", Markdown.ToText("### Title\n**Bold** and *em* with `code` and [a link](https://x.test).\n* item"));
        Assert.Equal("", Markdown.ToText(null));
    }

    [Theory]
    [InlineData("https://example.test/p.jpg", "https://example.test/p.jpg")]
    [InlineData("HTTP://example.test", "http://example.test/")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("ftp://example.test/x", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void SafeLink_accepts_only_web_schemes(string? input, string? expected) => Assert.Equal(expected, Markdown.SafeLink(input));
}

public sealed class CfpPackTests
{
    [Fact]
    public void Pack_follows_the_form_order_and_includes_the_speaker()
    {
        var talk = new Talk
        {
            Id = "owner-slides-agents", LocalId = "agents", SourceId = "owner/slides", Path = "ai/agents", Title = "Agents everywhere",
            Level = "intermediate", Durations = [45, 60], Tags = ["ai", "dotnet"], Abstract = "Long **abstract**.\n\nSecond paragraph.",
            Parts = [new TalkPart("Short abstract", "Short one."), new TalkPart("Outline", "- intro\n- demo"), new TalkPart("Takeaways", "- ship it")],
        };
        var speaker = new Speaker { SourceId = "owner/slides", Name = "Ada", Tagline = "Engineer", Bio = "Ada builds things.", Parts = [new TalkPart("Short bio", "Ada, engineer.")], Links = [new("github", "https://github.com/ada")], Photo = "https://raw.githubusercontent.com/o/r/HEAD/ada.jpg" };

        var md = CfpPack.Build(talk, speaker, markdown: true);
        var order = new[] { "# Agents everywhere", "## Abstract", "Long **abstract**.", "## Short abstract", "## Format", "45 min / 60 min · Intermediate", "## Tags", "ai, dotnet", "## Outline", "## Takeaways", "## Speaker", "**Ada** — Engineer", "Ada builds things.", "### Short bio", "- github: https://github.com/ada", "Photo: https://raw.githubusercontent.com/o/r/HEAD/ada.jpg" };
        var last = -1;
        foreach (var s in order) { var i = md.IndexOf(s, StringComparison.Ordinal); Assert.True(i > last, $"'{s}' missing or out of order"); last = i; }

        var txt = CfpPack.Build(talk, speaker, markdown: false);
        Assert.StartsWith("Agents everywhere\n\nAbstract\nLong abstract.", txt);
        Assert.DoesNotContain("**", txt);
        Assert.DoesNotContain("## ", txt);
    }

    [Fact]
    public void A_talk_bio_overrides_the_speaker_bio()
    {
        var talk = new Talk { Id = "o-s-t", LocalId = "t", SourceId = "o/s", Path = "t", Title = "T", Abstract = "A", Parts = [new TalkPart("Bio", "Historic bio.")] };
        var md = CfpPack.Build(talk, new Speaker { SourceId = "o/s", Name = "Ada", Bio = "Current bio." }, markdown: true);
        Assert.Contains("Historic bio.", md);
        Assert.DoesNotContain("Current bio.", md);
    }
}

[Collection(nameof(WebCollection))]
public sealed class TalkPagesTests(PodiumWebFactory app)
{
    private async Task<(Talk Public, Talk Private, Deck PublicDeck, Deck PrivateDeck)> SeedAsync(string suffix)
    {
        var talks = app.Services.GetRequiredService<ITalkStore>();
        var decks = app.Services.GetRequiredService<IDeckStore>();
        var pubDeck = await app.SeedDeckAsync($"talk-pub-{suffix}", Visibility.Public);
        var privDeck = await app.SeedDeckAsync($"talk-priv-{suffix}");
        var pub = new Talk
        {
            Id = $"owner-slides-pub-{suffix}", LocalId = $"pub-{suffix}", SourceId = "owner/slides", Path = pubDeck.Path, Title = $"Public talk {suffix}", Public = true,
            Level = "advanced", Durations = [45], Tags = ["public-tag"], Abstract = $"Public abstract {suffix} with <script>alert(1)</script> inside.\n\nMore.",
            Parts = [new TalkPart("Short abstract", "Short public."), new TalkPart("Outline", "- one")],
            DeckSlugs = [pubDeck.Slug, privDeck.Slug],
            Submissions =
            [
                new Submission { Key = "2025-01-01-ndc", Event = "NDC", Date = new DateOnly(2025, 1, 1), Status = "delivered", DeckSlug = pubDeck.Slug, Url = "https://ndc.test/talk", Recording = "javascript:alert(1)" },
                new Submission { Key = "2025-02-01-secret", Event = "Secret pitch", Date = new DateOnly(2025, 2, 1), Status = "submitted", Notes = "internal note" },
                new Submission { Key = "2025-03-01-declined", Event = "Declined conf", Date = new DateOnly(2025, 3, 1), Status = "declined" },
            ],
        };
        var priv = new Talk { Id = $"owner-slides-priv-{suffix}", LocalId = $"priv-{suffix}", SourceId = "owner/slides", Path = privDeck.Path, Title = $"Private talk {suffix}", Abstract = $"Private abstract {suffix}.", DeckSlugs = [privDeck.Slug] };
        await talks.UpsertAsync(pub);
        await talks.UpsertAsync(priv);
        await decks.UpsertAsync(pubDeck with { TalkId = pub.Id, CurrentHasText = true });
        await decks.UpsertAsync(privDeck with { TalkId = pub.Id, Variant = "workshop", CurrentHasText = true });
        await talks.UpsertSpeakerAsync(new Speaker { SourceId = "owner/slides", Name = "Ada Lovelace", Tagline = "Engineer", Photo = "photo.jpg", Bio = "Ada **builds** things.", Parts = [new TalkPart("Short bio", "Ada, engineer.")], Links = [new("github", "https://github.com/ada"), new("evil", "javascript:alert(1)")] });
        return (pub, priv, pubDeck, privDeck);
    }

    [Fact]
    public async Task Owner_catalog_shows_everything_and_the_public_page_only_public_facts()
    {
        var (pub, priv, pubDeck, privDeck) = await SeedAsync("a");
        var owner = await app.OwnerClientAsync();

        var catalog = await owner.SendAsync(PodiumWebFactory.Navigation("/talks"));
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        var html = await catalog.Content.ReadAsStringAsync();
        Assert.Contains(pub.Title, html);
        Assert.Contains(priv.Title, html);
        Assert.Contains("New talk", html);
        Assert.Contains("filename=abstract.md", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);

        var detail = await owner.SendAsync(PodiumWebFactory.Navigation($"/talks/{pub.Id}"));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailHtml = await detail.Content.ReadAsStringAsync();
        Assert.Contains("Secret pitch", detailHtml);
        Assert.Contains("internal note", detailHtml);
        Assert.Contains("Declined conf", detailHtml);
        Assert.Contains($"/decks/{privDeck.Slug}", detailHtml);
        Assert.Contains("badge-variant\">workshop", detailHtml);
        Assert.Contains($"data-compare-from=\"{pubDeck.Slug}\"", detailHtml); // compare select (both decks have text)
        Assert.Contains($"<option value=\"{privDeck.Slug}\">", detailHtml);
        Assert.Contains("https://ndc.test/talk", detailHtml);
        Assert.DoesNotContain("javascript:alert(1)", detailHtml); // unsafe recording link dropped
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", detailHtml);
        Assert.Contains("/edit/HEAD/" + pub.Path + "/abstract.md", detailHtml);
        Assert.Contains("filename=", detailHtml); // add-event new-file link

        var anon = app.Client();
        var speakerPage = await anon.SendAsync(PodiumWebFactory.Navigation("/talks"));
        Assert.Equal(HttpStatusCode.OK, speakerPage.StatusCode);
        var pageHtml = await speakerPage.Content.ReadAsStringAsync();
        Assert.Contains("Ada Lovelace", pageHtml);
        Assert.Contains("Ada <strong>builds</strong> things.", pageHtml);
        Assert.Contains("/talks/photo/owner/slides?v=", pageHtml); // repo-relative photo served by Podium (private repos work)
        Assert.DoesNotContain("raw.githubusercontent.com", pageHtml);
        Assert.Contains("https://github.com/ada", pageHtml);
        Assert.DoesNotContain("javascript:", pageHtml);
        Assert.Contains(pub.Title, pageHtml);
        Assert.DoesNotContain(priv.Title, pageHtml);
        Assert.DoesNotContain("New talk", pageHtml);
        Assert.Contains("og:title", pageHtml);
        // Razor that fell out of a code block would render as text; neither page may show any.
        Assert.DoesNotMatch(@"(^|\n)\s*(else|\}|@if)\b", pageHtml.Replace("<", "\n<"));
        Assert.DoesNotMatch(@"(^|\n)\s*(else|\}|@if)\b", html.Replace("<", "\n<"));
        Assert.Contains("id=\"talk-group\"", html);       // catalog controls for the owner
        Assert.Contains("value=\"status\"", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(pageHtml, "id=\"talk-search\"")); // one search box on the public page with several talks

        var publicDetail = await anon.SendAsync(PodiumWebFactory.Navigation($"/talks/{pub.Id}"));
        Assert.Equal(HttpStatusCode.OK, publicDetail.StatusCode);
        var publicHtml = await publicDetail.Content.ReadAsStringAsync();
        Assert.Contains("NDC", publicHtml);
        Assert.DoesNotContain("Secret pitch", publicHtml); // submitted, not a public fact
        Assert.DoesNotContain("internal note", publicHtml);
        Assert.DoesNotContain("Declined conf", publicHtml);
        Assert.Contains($"/d/{pubDeck.Slug}/", publicHtml);
        Assert.DoesNotContain(privDeck.Slug, publicHtml); // private variant stays invisible
        Assert.DoesNotContain("Edit on GitHub", publicHtml);
        Assert.Contains("og:description", publicHtml);
        Assert.Contains("Short public.", publicHtml);

        Assert.Equal(HttpStatusCode.NotFound, (await anon.SendAsync(PodiumWebFactory.Navigation($"/talks/{priv.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.SendAsync(PodiumWebFactory.Navigation("/talks/does-not-exist"))).StatusCode);

        // The owner's preview shows the public rendering, with a way back.
        var preview = await owner.SendAsync(PodiumWebFactory.Navigation("/talks?public=1"));
        var previewHtml = await preview.Content.ReadAsStringAsync();
        Assert.Contains("how organisers see", previewHtml);
        Assert.DoesNotContain(priv.Title, previewHtml);
    }

    [Fact]
    public async Task Cfp_pack_is_owner_only_and_search_finds_abstracts()
    {
        var (pub, _, _, _) = await SeedAsync("b");
        var owner = await app.OwnerClientAsync();
        var md = await owner.GetAsync($"/talks/{pub.Id}/cfp.md");
        Assert.Equal(HttpStatusCode.OK, md.StatusCode);
        Assert.Equal("text/markdown", md.Content.Headers.ContentType!.MediaType);
        var body = await md.Content.ReadAsStringAsync();
        Assert.StartsWith($"# {pub.Title}", body);
        Assert.Contains("## Speaker", body);
        Assert.Contains("Ada Lovelace", body);
        var txt = await owner.GetAsync($"/talks/{pub.Id}/cfp.txt");
        Assert.Equal("text/plain", txt.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("**", await txt.Content.ReadAsStringAsync());

        var anon = app.Client();
        Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync($"/talks/{pub.Id}/cfp.md")).StatusCode);
        var guest = await app.GuestClientAsync(990777);
        Assert.Equal(HttpStatusCode.Redirect, (await guest.GetAsync($"/talks/{pub.Id}/cfp.txt")).StatusCode);

        var hits = await owner.GetFromJsonAsync<List<TalkHitDto>>("/api/talks/search?q=public%20abstract%20b");
        Assert.NotNull(hits);
        Assert.Contains(hits, h => h.Id == pub.Id && h.Snippet.Contains("Public abstract", StringComparison.Ordinal));
        Assert.Empty((await owner.GetFromJsonAsync<List<TalkHitDto>>("/api/talks/search?q=zzzz-nothing"))!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/talks/search?q=public")).StatusCode);
    }

    [Fact]
    public async Task Library_and_deck_page_surface_the_talk()
    {
        var (pub, _, pubDeck, privDeck) = await SeedAsync("c");
        var owner = await app.OwnerClientAsync();
        var library = await (await owner.SendAsync(PodiumWebFactory.Navigation("/"))).Content.ReadAsStringAsync();
        Assert.Contains("<option value=\"talk\">Talk</option>", library);
        Assert.Contains($"data-talk=\"{pub.Title}\"", library);
        Assert.Contains("badge-variant\" href=\"/talks/" + pub.Id + "\" title=\"Variant of the talk", library);
        Assert.Contains(">workshop</a>", library);
        Assert.Contains(">main</a>", library);

        var deckPage = await (await owner.SendAsync(PodiumWebFactory.Navigation($"/decks/{pubDeck.Slug}"))).Content.ReadAsStringAsync();
        Assert.Contains("id=\"talk\"", deckPage);
        Assert.Contains($"/talks/{pub.Id}", deckPage);
        Assert.Contains($"/decks/{privDeck.Slug}", deckPage); // sibling variant
        Assert.Contains("Given with this deck:", deckPage);
        Assert.Contains("NDC", deckPage);
        Assert.Contains($"/decks/{pubDeck.Slug}/compare/{privDeck.Slug}", deckPage);
    }

    [Fact]
    public async Task Compare_view_aligns_two_variants_by_slide_text()
    {
        var (_, _, pubDeck, privDeck) = await SeedAsync("d");
        app.Artifacts.PutArtifact(pubDeck.Slug, pubDeck.CurrentBuildId!, ArtifactKind.Text, "application/json", System.Text.Encoding.UTF8.GetBytes("[{\"index\":1,\"title\":\"Intro\",\"text\":\"welcome to agents\"},{\"index\":2,\"title\":\"Loop\",\"text\":\"an agent is a loop with tools\"},{\"index\":3,\"text\":\"demo time\"}]"));
        app.Artifacts.PutArtifact(privDeck.Slug, privDeck.CurrentBuildId!, ArtifactKind.Text, "application/json", System.Text.Encoding.UTF8.GetBytes("[{\"index\":1,\"title\":\"Intro\",\"text\":\"welcome to agents\"},{\"index\":2,\"title\":\"Loop\",\"text\":\"an agent is a loop with tools and memory\"},{\"index\":3,\"text\":\"exercise one\"}]"));
        app.Artifacts.PutArtifact(pubDeck.Slug, pubDeck.CurrentBuildId!, ArtifactKind.SlideSheetMeta, "application/json", System.Text.Encoding.UTF8.GetBytes("{\"count\":3,\"cols\":3,\"rows\":1,\"cellWidth\":320,\"cellHeight\":180}"));
        var decks = app.Services.GetRequiredService<IDeckStore>();
        await decks.UpsertAsync((await decks.GetAsync(pubDeck.Slug))! with { CurrentHasSlideSheet = true });

        var owner = await app.OwnerClientAsync();
        var page = await owner.SendAsync(PodiumWebFactory.Navigation($"/decks/{pubDeck.Slug}/compare/{privDeck.Slug}"));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("badge-cmp-same\">1</span> identical", html);
        Assert.Contains("badge-cmp-changed\">1</span> reworded", html);
        Assert.Contains("badge-cmp-left\">1</span> only in", html);
        Assert.Contains("badge-cmp-right\">1</span> only in", html);
        Assert.Contains("<ins>and</ins>", html);
        Assert.Contains("<ins>memory</ins>", html);
        Assert.Contains($"background-image:url(&#x27;/d/{pubDeck.Slug}/slides.jpg?v=", html); // sprite from the slide sheet
        Assert.Contains($"/d/{privDeck.Slug}/3", html); // deep link to the slide only in the variant

        // Same deck twice goes back to the deck page; unknown decks 404; guests are kept out.
        var self = await owner.SendAsync(PodiumWebFactory.Navigation($"/decks/{pubDeck.Slug}/compare/{pubDeck.Slug}"));
        Assert.Equal(HttpStatusCode.Redirect, self.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.SendAsync(PodiumWebFactory.Navigation($"/decks/{pubDeck.Slug}/compare/nope-nope"))).StatusCode);
        var guest = await app.GuestClientAsync(990778);
        Assert.Equal(HttpStatusCode.Redirect, (await guest.SendAsync(PodiumWebFactory.Navigation($"/decks/{pubDeck.Slug}/compare/{privDeck.Slug}"))).StatusCode);
    }

    [Fact]
    public async Task Speaker_photo_is_read_from_the_repository_and_served_only_when_it_is_an_image()
    {
        await SeedAsync("e"); // speaker.md photo: photo.jpg (repo-relative)
        var anon = app.Client();
        app.Repository.Files["photo.jpg"] = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
        var photo = await anon.GetAsync("/talks/photo/owner/slides");
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal("image/jpeg", photo.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", photo.Headers.GetValues("X-Content-Type-Options").Single());
        var reads = app.Repository.Reads;
        await anon.GetAsync("/talks/photo/owner/slides");
        Assert.Equal(reads, app.Repository.Reads); // cached

        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/talks/photo/stranger/public-deck")).StatusCode); // untrusted source
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/talks/photo/nobody/nothing")).StatusCode);

        // A text file at the photo path is not served as an image.
        var talks = app.Services.GetRequiredService<ITalkStore>();
        await talks.UpsertSpeakerAsync((await talks.GetSpeakerAsync("owner/slides"))! with { Photo = "evil.svg" });
        app.Repository.Files["evil.svg"] = System.Text.Encoding.UTF8.GetBytes("<svg onload=\"alert(1)\"></svg>");
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/talks/photo/owner/slides")).StatusCode);
        await talks.UpsertSpeakerAsync((await talks.GetSpeakerAsync("owner/slides"))! with { Photo = "photo.jpg" });

        // While no talk of the source is public, visitors get nothing (the owner still sees the photo on the catalog).
        var publicTalks = (await talks.ListBySourceAsync("owner/slides")).Where(t => t.Public).ToList();
        foreach (var t in publicTalks) await talks.UpsertAsync(t with { Public = false });
        try
        {
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/talks/photo/owner/slides")).StatusCode);
            var owner = await app.OwnerClientAsync();
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/talks/photo/owner/slides")).StatusCode);
        }
        finally { foreach (var t in publicTalks) await talks.UpsertAsync(t); }
    }

    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("/assets/me.png", "assets/me.png")]
    [InlineData("assets\\me.png", "assets/me.png")]
    [InlineData("../secret.png", null)]
    [InlineData("C:/x.png", null)]
    [InlineData("", null)]
    public void Repo_paths_are_normalised_and_never_traverse(string input, string? expected) => Assert.Equal(expected, Podium.Core.Discovery.TalkFiles.RepoPath(input));

    [Fact]
    public async Task Library_cards_carry_the_dates_and_facets_the_sorting_and_grouping_run_on()
    {
        var (pub, _, pubDeck, privDeck) = await SeedAsync("f");
        var decks = app.Services.GetRequiredService<IDeckStore>();
        var authored = new DateTimeOffset(2019, 10, 30, 7, 51, 0, TimeSpan.Zero);
        await decks.UpsertAsync((await decks.GetAsync(pubDeck.Slug))! with { AuthoredAt = authored, DocumentCreatedAt = new DateTimeOffset(2017, 1, 29, 10, 0, 0, TimeSpan.Zero), LastCommitAt = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero), Path = "talks/" + pubDeck.Slug });
        await decks.UpsertAsync((await decks.GetAsync(privDeck.Slug))! with { LastCommitAt = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero) });

        var owner = await app.OwnerClientAsync();
        var html = await (await owner.SendAsync(PodiumWebFactory.Navigation("/"))).Content.ReadAsStringAsync();
        var card = html[html.IndexOf($"data-slug=\"{pubDeck.Slug}\"", StringComparison.Ordinal)..];
        card = card[..card.IndexOf("</article>", StringComparison.Ordinal)];
        Assert.Contains("data-saved=\"2019-10-30T07:51:00.0000000Z\"", card);      // the document's own date, not the import commit
        Assert.Contains("data-created=\"2017-01-29T10:00:00.0000000Z\"", card);
        Assert.Contains("data-committed=\"2026-10-06T00:00:00.0000000Z\"", card);
        Assert.Contains("data-year=\"2019\"", card);
        Assert.Contains("data-year-created=\"2017\"", card);
        Assert.Contains("data-given=\"2025-01-01\"", card);                           // the NDC delivery named this deck
        Assert.Contains("data-year-given=\"2025\"", card);
        Assert.Contains("data-section=\"talks\"", card);
        Assert.Contains("data-talk-status=\"Available talk\"", card);
        Assert.Contains(">saved <time", card);

        var other = html[html.IndexOf($"data-slug=\"{privDeck.Slug}\"", StringComparison.Ordinal)..];
        other = other[..other.IndexOf("</article>", StringComparison.Ordinal)];
        Assert.Contains(">updated <time", other);                                     // no document date: the commit stands in
        Assert.Contains("data-given=\"\"", other);

        Assert.Contains("<option value=\"section\">Folder</option>", html);
        Assert.Contains("<option value=\"yeargiven\">Year given</option>", html);
        Assert.Contains("<option value=\"saved\">Last saved</option>", html);
        Assert.Contains("<option value=\"committed\">Last committed</option>", html);
        Assert.Contains("id=\"sort-dir\"", html);
    }

    private sealed record TalkHitDto(string Id, string Title, string Status, int Decks, int Events, string Snippet, int Score);
}
