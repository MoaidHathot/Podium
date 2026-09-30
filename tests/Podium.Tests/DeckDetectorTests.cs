using Podium.Core.Discovery;
using Podium.Core.Models;

namespace Podium.Tests;

public class DeckDetectorTests
{
    [Fact]
    public void Detects_slidev_presenterm_gitpitch_and_static_decks()
    {
        var paths = new[]
        {
            "Microsoft/casual/ai/How-I-ended-up-with-over-75-AI-agents/slides.md",
            "Microsoft/casual/ai/How-I-ended-up-with-over-75-AI-agents/package.json",
            "Microsoft/casual/ai/How-I-ended-up-with-over-75-AI-agents/components/Fleet.vue",
            "Microsoft/casual/ai/How-I-ended-up-with-over-75-AI-agents/node_modules/@slidev/cli/slides.md",
            "Microsoft/casual/ai/How-I-ended-up-with-over-75-AI-agents/dist/index.html",
            "Microsoft/casual/ai/ai-for-productivity/main.md",
            "Microsoft/casual/ai/ai-for-productivity/config.yaml",
            "Microsoft/casual/ai/ai-for-productivity/ai-for-productivity.html",
            "Microsoft/casual/ai/ai-for-productivity/ai-for-productivity.pdf",
            "CodeValue/Sessions/Async/PITCHME.md",
            "CodeValue/Sessions/Async/PITCHME.yaml",
            "meetups/old-talk/old-talk.html",
            "meetups/old-talk/old-talk.pdf",
            "README.md",
        };

        var decks = DeckDetector.Detect(paths);

        Assert.Equal(3, decks.Count);
        Assert.Contains(decks, d => d.Kind == DeckKind.Slidev && d.Path == "Microsoft/casual/ai/How-I-ended-up-with-over-75-AI-agents" && d.Entry == "slides.md");
        Assert.Contains(decks, d => d.Kind == DeckKind.Presenterm && d.Path == "Microsoft/casual/ai/ai-for-productivity" && d.Entry == "main.md");
        Assert.DoesNotContain(decks, d => d.Kind == DeckKind.GitPitch); // legacy GitPitch is skipped
        Assert.Contains(decks, d => d.Kind == DeckKind.Static && d.Path == "meetups/old-talk" && d.Entry == "old-talk.html");
    }

    [Fact]
    public void Root_level_slidev_deck_has_empty_path()
    {
        var decks = DeckDetector.Detect(["slides.md", "package.json", "public/logo.png"]);
        var d = Assert.Single(decks);
        Assert.Equal("", d.Path);
        Assert.Equal(DeckKind.Slidev, d.Kind);
    }

    [Fact]
    public void Presenterm_with_dated_markdown_picks_a_markdown_entry()
    {
        var decks = DeckDetector.Detect(["talk/config.yaml", "talk/2025-11-18.md", "talk/README.md"]);
        var d = Assert.Single(decks);
        Assert.Equal(DeckKind.Presenterm, d.Kind);
        Assert.Equal("2025-11-18.md", d.Entry);
    }

    [Fact]
    public void Ignores_windows_separators_and_ignored_folders()
    {
        var decks = DeckDetector.Detect([@"a\b\slides.md", @"a\b\.slidev\x.md", @"a\b\node_modules\y\slides.md"]);
        var d = Assert.Single(decks);
        Assert.Equal("a/b", d.Path);
    }
}
