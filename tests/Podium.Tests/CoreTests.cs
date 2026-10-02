using Podium.Core;
using Podium.Core.Discovery;
using Podium.Core.Models;
using Podium.Core.Security;

namespace Podium.Tests;

public class DeckMetadataReaderTests
{
    [Fact]
    public void Reads_slidev_headmatter()
    {
        const string md = """
            ---
            theme: default
            title: How I ended up with over 75 AI agents
            author: Moaid Hathot
            info: |
              A talk about **agents**.
            tags: [ai, agents]
            colorSchema: dark
            ---

            # Something else

            ---

            ```ts {monaco-run}
            console.log(1)
            ```
            """;

        var m = DeckMetadataReader.ReadSlidev(md);

        Assert.Equal("How I ended up with over 75 AI agents", m.Title);
        Assert.Equal("Moaid Hathot", m.Author);
        Assert.Equal("A talk about agents.", m.Description);
        Assert.Equal(["ai", "agents"], m.Tags);
        Assert.Single(m.UnsupportedFeatures);
    }

    [Fact]
    public void Falls_back_to_first_heading_and_handles_crlf()
    {
        var md = "---\r\ntheme: seriph\r\n---\r\n\r\n# **Hello** World\r\n\r\n---\r\n\r\n# Second\r\n";
        var m = DeckMetadataReader.ReadSlidev(md);
        Assert.Equal("Hello World", m.Title);
        Assert.Null(m.Author);
        Assert.Empty(m.UnsupportedFeatures);
    }

    [Fact]
    public void No_frontmatter_is_fine()
    {
        var m = DeckMetadataReader.ReadSlidev("# Just a title\n\ntext");
        Assert.Equal("Just a title", m.Title);
    }

    [Fact]
    public void Broken_yaml_does_not_throw()
    {
        var m = DeckMetadataReader.ReadSlidev("---\ntitle: [unclosed\n---\n# Fallback\n");
        Assert.Equal("Fallback", m.Title);
    }

    [Fact]
    public void Reads_presenterm_frontmatter_and_flags_exec()
    {
        const string md = """
            ---
            title: Intro to MCP
            sub_title: The protocol
            author: Moaid
            ---

            ```bash +exec
            echo hi
            ```
            """;
        var m = DeckMetadataReader.ReadPresenterm(md);
        Assert.Equal("Intro to MCP", m.Title);
        Assert.Equal("The protocol", m.Description);
        Assert.Single(m.UnsupportedFeatures);
    }

    [Fact]
    public void Presenterm_without_title_uses_bold_line_of_first_slide_not_later_headings()
    {
        const string md = """
            ---
            theme:
              name: catppuccin-frappe
            ---
            <!-- newlines: 8 -->
            **Autonomous Agents with GitHub Copilot SDK**
            ---
            <span class="noice">Moaid Hathot</span>
            <!-- end_slide -->

            # Moaid Hathot

            Principal engineer
            """;
        var m = DeckMetadataReader.ReadPresenterm(md);
        Assert.Equal("Autonomous Agents with GitHub Copilot SDK", m.Title);
    }
}

public class DeckConfigTests
{
    [Fact]
    public void Parses_all_fields_and_normalises_alias_and_tags()
    {
        var cfg = DeckConfig.Parse("""
            title: My Talk
            alias: "My Talk 2026!"
            tags: [AI, agents, AI]
            exportPdf: false
            export_pptx: true
            stripNotes: no
            visibility: public
            npmScripts: true
            """);
        Assert.NotNull(cfg);
        Assert.Equal("My Talk", cfg!.Title);
        Assert.Equal("my-talk-2026", cfg.Alias);
        Assert.Equal(["ai", "agents"], cfg.Tags);
        Assert.False(cfg.ExportPdf);
        Assert.True(cfg.ExportPptx);
        Assert.False(cfg.StripNotes);
        Assert.Equal(Visibility.Public, cfg.Visibility);
        Assert.True(cfg.NpmScripts);
    }

    [Fact]
    public void Invalid_or_non_mapping_yaml_yields_null()
    {
        Assert.Null(DeckConfig.Parse("- just\n- a list"));
        Assert.Null(DeckConfig.Parse("title: [unclosed"));
    }
}

public class SlugTests
{
    [Theory]
    [InlineData("Slides", "Microsoft/casual/ai/How-I-ended-up-with-over-75-AI-agents", "slides-how-i-ended-up-with-over-75-ai-agents")]
    [InlineData("Slides", "", "slides")]
    [InlineData("My Repo", "Talks/Ünïcödé & Stuff!!", "my-repo-unicode-stuff")]
    [InlineData("x", "---", "x")]
    public void Builds_url_safe_slugs(string repo, string path, string expected)
    {
        var slug = Slug.ForDeck(repo, path);
        Assert.Equal(expected, slug);
        Assert.True(Slug.IsValid(slug));
    }

    [Theory]
    [InlineData("ok-slug", true)]
    [InlineData("-leading", false)]
    [InlineData("UPPER", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    [InlineData("../etc", false)]
    public void Validates_slugs(string slug, bool valid) => Assert.Equal(valid, Slug.IsValid(slug));
}

public class AccessPolicyTests
{
    private static Deck Deck(Visibility site = Visibility.Private, Visibility pdf = Visibility.Private, bool archived = false) => new()
    {
        Slug = "d", SourceId = "s", Path = "", Entry = "slides.md", Kind = DeckKind.Slidev,
        Visibility = site, PdfVisibility = pdf, Archived = archived,
    };

    private static readonly Caller Owner = new("github:1", true, "me");
    private static readonly Caller Guest = new("github:2", false, "guest");

    [Fact]
    public void Owner_can_do_anything_except_archived_is_still_visible_to_owner()
    {
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Evaluate(Deck(archived: true), ArtifactKind.Site, Owner, [], false));
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Evaluate(Deck(), ArtifactKind.Log, Owner, [], false));
    }

    [Fact]
    public void Archived_denied_for_everyone_else_even_if_public()
    {
        Assert.Equal(AccessDecision.Deny, AccessPolicy.Evaluate(Deck(site: Visibility.Public, archived: true), ArtifactKind.Site, Caller.Anonymous, [], false));
    }

    [Fact]
    public void Private_requires_login_then_denies_guests()
    {
        Assert.Equal(AccessDecision.RequireLogin, AccessPolicy.Evaluate(Deck(), ArtifactKind.Site, Caller.Anonymous, [], false));
        Assert.Equal(AccessDecision.Deny, AccessPolicy.Evaluate(Deck(), ArtifactKind.Site, Guest, [], false));
    }

    [Fact]
    public void Public_site_does_not_make_pdf_public()
    {
        var d = Deck(site: Visibility.Public);
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Evaluate(d, ArtifactKind.Site, Caller.Anonymous, [], false));
        Assert.Equal(AccessDecision.RequireLogin, AccessPolicy.Evaluate(d, ArtifactKind.Pdf, Caller.Anonymous, [], false));
        Assert.Equal(AccessDecision.Deny, AccessPolicy.Evaluate(d, ArtifactKind.Pdf, Guest, [], false));
    }

    [Fact]
    public void Shared_honours_grants_per_artifact()
    {
        var d = Deck(site: Visibility.Shared, pdf: Visibility.Shared);
        var grants = new[] { new Grant { DeckSlug = "d", Principal = "github:2", Site = true, Pdf = false } };
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Evaluate(d, ArtifactKind.Site, Guest, grants, false));
        Assert.Equal(AccessDecision.Deny, AccessPolicy.Evaluate(d, ArtifactKind.Pdf, Guest, grants, false));
        Assert.Equal(AccessDecision.Deny, AccessPolicy.Evaluate(d, ArtifactKind.Site, new Caller("github:3", false, null), grants, false));
        Assert.Equal(AccessDecision.RequireLogin, AccessPolicy.Evaluate(d, ArtifactKind.Site, Caller.Anonymous, grants, false));
    }

    [Fact]
    public void Valid_share_link_allows_anonymous_even_on_private()
    {
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Evaluate(Deck(), ArtifactKind.Site, Caller.Anonymous, [], validShareLink: true));
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Evaluate(Deck(site: Visibility.Link), ArtifactKind.Site, Caller.Anonymous, [], validShareLink: true));
        Assert.Equal(AccessDecision.RequireLogin, AccessPolicy.Evaluate(Deck(site: Visibility.Link), ArtifactKind.Site, Caller.Anonymous, [], validShareLink: false));
    }

    [Fact]
    public void Logs_are_owner_only_regardless_of_visibility()
    {
        Assert.Equal(AccessDecision.Deny, AccessPolicy.Evaluate(Deck(site: Visibility.Public), ArtifactKind.Log, Guest, [], true));
    }
}
