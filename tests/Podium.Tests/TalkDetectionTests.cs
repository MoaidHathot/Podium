using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.Discovery;
using Podium.Core.InMemory;
using Podium.Core.Models;
using Podium.Core.Services;

namespace Podium.Tests;

public class TalkDetectionTests
{
    [Fact]
    public void Slidev_variant_files_become_variant_decks_and_reserved_markdown_is_never_a_deck()
    {
        var decks = DeckDetector.Detect([
            "talks/agents/slides.md", "talks/agents/slides.dotnet-conf-2025.md", "talks/agents/slides.NDC.Oslo.md", "talks/agents/abstract.md", "talks/agents/README.md",
            "talks/agents/submissions/2025-11-18-dotnet-conf-il.md", "talks/agents/submissions/notes.pdf",
        ]);
        Assert.Equal(3, decks.Count);
        Assert.Equal((null, "slides.md"), (decks[0].Variant, decks[0].Entry));
        Assert.Contains(decks, d => d.Variant == "dotnet-conf-2025" && d.Entry == "slides.dotnet-conf-2025.md" && d.Kind == DeckKind.Slidev);
        Assert.Contains(decks, d => d.Variant == "ndc-oslo" && d.Entry == "slides.NDC.Oslo.md");
        Assert.DoesNotContain(decks, d => d.Path.EndsWith("submissions", StringComparison.Ordinal)); // a PDF under submissions/ of a talk is not a deck
    }

    [Fact]
    public void Presenterm_newest_dated_file_is_main_and_the_others_are_variants()
    {
        var decks = DeckDetector.Detect(["t/config.yaml", "t/2025_07_30.md", "t/2025_11_18.md", "t/2025-03-01.md", "t/abstract.md", "t/README.md"]);
        Assert.Equal(3, decks.Count);
        Assert.Equal("2025_11_18.md", decks.Single(d => d.Variant is null).Entry);
        Assert.Contains(decks, d => d.Variant == "2025-07-30" && d.Entry == "2025_07_30.md");
        Assert.Contains(decks, d => d.Variant == "2025-03-01");
        // main.md wins over dates; a single undated file is the main deck; dates can be compact.
        Assert.Equal("main.md", DeckDetector.PickPresentermEntry(["2025_11_18.md", "main.md"]));
        Assert.Equal("talk.md", DeckDetector.PickPresentermEntry(["talk.md"]));
        Assert.Equal("20251118-ndc.md", DeckDetector.PickPresentermEntry(["20251118-ndc.md", "20250730-meetup.md"]));
        Assert.Equal(new DateOnly(2025, 11, 18), DeckDetector.DateOf("20251118-ndc.md"));
        Assert.Null(DeckDetector.DateOf("v2025-notes.md"));
    }

    [Fact]
    public void Talks_and_speaker_are_detected_from_their_files()
    {
        var talks = DeckDetector.DetectTalks(["a/b/abstract.md", "a/b/submissions/2024-01-01-x.md", "a/b/submissions/README.md", "a/b/submissions/image.png", "c/abstract.md", "speaker.md", "node_modules/x/abstract.md"]);
        Assert.Equal(2, talks.Count);
        Assert.Equal(["2024-01-01-x.md"], talks[0].SubmissionFiles);
        Assert.Equal("a/b/submissions/2024-01-01-x.md", talks[0].SubmissionPath("2024-01-01-x.md"));
        Assert.Empty(talks[1].SubmissionFiles);
        Assert.Equal("speaker.md", DeckDetector.DetectSpeaker(["x/speaker.md", "speaker.md"]));
        Assert.Null(DeckDetector.DetectSpeaker(["x/speaker.md"]));
    }

    [Fact]
    public void Abstract_file_parses_front_matter_body_and_parts()
    {
        var talk = TalkFiles.ReadTalk("""
            ---
            title: How I ended up with over 75 AI agents
            level: Intermediate
            duration: [45, 60m, 45]
            status: Available
            public: yes
            tags: [AI, agents, ai]
            ---
            Canonical abstract, first paragraph.

            Second paragraph with `code`.

            ## Short abstract
            One hundred words.

            ## Outline
            - a
            - b

            ```md
            ## not a heading
            ```

            ## Bio
            Old bio kept for history.
            """, "Slides", "Microsoft/casual/ai/How-I-ended-up-with-over-75-AI-agents", "owner/slides")!;
        Assert.Equal("slides-how-i-ended-up-with-over-75-ai-agents", talk.Id);
        Assert.Equal("how-i-ended-up-with-over-75-ai-agents", talk.LocalId);
        Assert.Equal("intermediate", talk.Level);
        Assert.Equal([45, 60], talk.Durations);
        Assert.Equal("available", talk.Status);
        Assert.True(talk.Public);
        Assert.Equal(["ai", "agents"], talk.Tags);
        Assert.StartsWith("Canonical abstract, first paragraph.", talk.Abstract);
        Assert.Contains("Second paragraph", talk.Abstract);
        Assert.Equal(["Short abstract", "Outline", "Bio"], talk.Parts.Select(p => p.Name));
        Assert.Contains("## not a heading", talk.Part("outline")!.Markdown); // fenced blocks are kept whole
        Assert.Equal("One hundred words.", talk.ShortAbstract);
        Assert.Equal("Canonical abstract, first paragraph.", Talk.FirstParagraph(talk.Abstract));

        var explicitId = TalkFiles.ReadTalk("---\nid: 75 Agents!\n---\nbody", "Slides", "x/y", "s")!;
        Assert.Equal("slides-75-agents", explicitId.Id);
        Assert.Equal("75-agents", explicitId.LocalId);
        Assert.Equal("", TalkFiles.ReadTalk("no front matter at all", "Slides", "x/y", "s")!.Title);
        Assert.Null(TalkFiles.ReadTalk("---\ntitle: [unclosed\n---\n", "Slides", "x/y", "s"));
    }

    [Fact]
    public void Submission_file_parses_dates_statuses_and_falls_back_to_the_file_name_date()
    {
        var s = TalkFiles.ReadSubmission("""
            ---
            event: .NET Conf Israel 2025
            date: 2025-11-18
            status: Delivered
            format: Talk
            duration: 45
            title: 75 agents later
            deck: slides.dotnet-conf-2025.md
            recording: https://youtu.be/abc
            url: javascript:alert(1)
            location: Tel Aviv
            ---
            As submitted.

            ## Bio
            Bio as submitted.
            """, "2025-11-18-dotnet-conf-il.md")!;
        Assert.Equal("2025-11-18-dotnet-conf-il", s.Key);
        Assert.Equal(new DateOnly(2025, 11, 18), s.Date);
        Assert.Equal("delivered", s.Status);
        Assert.Equal("talk", s.Format);
        Assert.Equal(45, s.Duration);
        Assert.Equal("75 agents later", s.Title);
        Assert.Equal("https://youtu.be/abc", s.Recording);
        Assert.Null(s.Url); // only http(s) links
        Assert.Equal("As submitted.", s.Abstract);
        Assert.Equal("Bio as submitted.", s.Part("bio")!.Markdown);
        Assert.True(s.IsPublicFact);

        var bare = TalkFiles.ReadSubmission("---\nevent: Meetup\nstatus: weird\n---\n", "2024-06-05-meetup.md")!;
        Assert.Equal(new DateOnly(2024, 6, 5), bare.Date); // from the file name
        Assert.Equal("submitted", bare.Status);
        Assert.Null(bare.Abstract);
        Assert.Equal(new DateOnly(2023, 4, 1), TalkFiles.ReadSubmission("---\ndate: April 2023\n---\n", "x.md")!.Date);
    }

    [Fact]
    public void Speaker_file_parses_links_and_bios()
    {
        var sp = TalkFiles.ReadSpeaker("""
            ---
            name: Moaid Hathot
            tagline: Senior Software Engineer
            photo: https://example.test/me.jpg
            links:
              github: https://github.com/MoaidHathot
              x: not-a-url
            ---
            Long bio paragraph.

            More.

            ## Short bio
            Short one.
            """, "owner/slides")!;
        Assert.Equal("Moaid Hathot", sp.Name);
        Assert.Equal([new KeyValuePair<string, string>("github", "https://github.com/MoaidHathot")], sp.Links);
        Assert.StartsWith("Long bio paragraph.", sp.Bio);
        Assert.Equal("Short one.", sp.ShortBio);
        Assert.Equal("Long bio paragraph.", Talk.FirstParagraph(TalkFiles.ReadSpeaker("Long bio paragraph.\n\nMore.", "s")!.Bio));
    }

    [Fact]
    public async Task Sync_indexes_talks_variants_members_and_submissions_and_keeps_session_links_across_edits()
    {
        var sources = new InMemorySourceStore();
        var decks = new InMemoryDeckStore();
        var buildsStore = new InMemoryBuildStore();
        var talks = new InMemoryTalkStore();
        var repo = new FakeRepo();
        var runner = new FakeRunner();
        var buildService = new BuildService(buildsStore, decks, new FakeArtifacts(), repo, runner, new FakeTokens(), Options.Create(new BuildOptions { PublicBaseUrl = new Uri("https://x.test") }), NullLogger<BuildService>.Instance, sources);
        var sync = new DeckSyncService(sources, decks, repo, buildService, NullLogger<DeckSyncService>.Instance, talks);
        var source = new Source { Id = "owner/slides", Owner = "owner", Repo = "Slides", Trusted = true, InstallationId = 1 };
        await sources.UpsertAsync(source);

        const string home = "Microsoft/casual/ai/agents";
        repo.Tree.AddRange([
            $"{home}/slides.md", $"{home}/slides.ndc.md", $"{home}/abstract.md", $"{home}/submissions/2025-11-18-dotnet-conf-il.md", $"{home}/submissions/2026-03-10-ndc-oslo.md",
            "meetups/agents-lightning/config.yaml", "meetups/agents-lightning/2025_07_30.md", "meetups/agents-lightning/2025_11_18.md", "meetups/agents-lightning/.podium.yml",
            "talks/debugging-jedi/abstract.md", "talks/debugging-jedi/submissions/2017-09-21-dotnet-summit-minsk.md", "talks/debugging-jedi/jedi.pdf",
            "speaker.md", "other/orphan/slides.md",
        ]);
        repo.Files[$"{home}/slides.md"] = "---\ntitle: 75 agents\n---\n";
        repo.Files[$"{home}/slides.ndc.md"] = "---\ntitle: 75 agents (NDC cut)\n---\n";
        repo.Files[$"{home}/abstract.md"] = "---\ntitle: How I ended up with over 75 AI agents\nid: agents\ntags: [ai]\n---\nAbstract.\n\n## Short abstract\nShort.";
        repo.Files[$"{home}/submissions/2025-11-18-dotnet-conf-il.md"] = "---\nevent: .NET Conf Israel 2025\nstatus: delivered\ndeck: slides.ndc.md\n---\n";
        repo.Files[$"{home}/submissions/2026-03-10-ndc-oslo.md"] = "---\nevent: NDC Oslo\nstatus: accepted\ndeck: slides-agents-lightning\n---\nAs submitted.";
        repo.Files["meetups/agents-lightning/2025_07_30.md"] = "---\ntitle: Agents in 10 minutes\n---\n";
        repo.Files["meetups/agents-lightning/2025_11_18.md"] = "---\ntitle: Agents in 10 minutes (v2)\n---\n";
        repo.Files["meetups/agents-lightning/.podium.yml"] = "talk: agents\n";
        repo.Files["talks/debugging-jedi/abstract.md"] = "---\ntitle: How to become a .NET debugging Jedi\nstatus: retired\n---\nJedi abstract.";
        repo.Files["talks/debugging-jedi/submissions/2017-09-21-dotnet-summit-minsk.md"] = "---\nevent: .NET Summit\nstatus: delivered\ndeck: jedi.pdf\n---\n";
        repo.Files["speaker.md"] = "---\nname: Moaid\n---\nBio.";
        repo.Files["other/orphan/slides.md"] = "---\ntitle: Orphan\n---\n";

        var result = await sync.SyncAsync(source);

        // Decks: the main Slidev deck, its variant, the presenterm main (newest dated) + variant, the PDF, the orphan.
        var all = await decks.ListAsync();
        var mainDeck = all.Single(d => d.Slug == "slides-agents");
        var ndc = all.Single(d => d.Slug == "slides-agents-ndc");
        Assert.Null(mainDeck.Variant);
        Assert.Equal("ndc", ndc.Variant);
        Assert.Equal("75 agents (NDC cut)", ndc.Title);
        var lightning = all.Single(d => d.Slug == "slides-agents-lightning");
        Assert.Equal("2025_11_18.md", lightning.Entry);
        var lightningOld = all.Single(d => d.Slug == "slides-agents-lightning-2025-07-30");
        Assert.Equal("2025_07_30.md", lightningOld.Entry);
        Assert.Equal("Agents in 10 minutes", lightningOld.Title);
        var orphan = all.Single(d => d.Slug == "slides-orphan");

        // Talks: ids, membership (folder decks first, then joined), submissions with resolved decks.
        var talk = (await talks.GetAsync("slides-agents"))!;
        Assert.Equal("agents", talk.LocalId);
        Assert.Equal(["slides-agents", "slides-agents-ndc", "slides-agents-lightning", "slides-agents-lightning-2025-07-30"], talk.DeckSlugs);
        Assert.All(new[] { mainDeck, ndc, lightning, lightningOld }, d => Assert.Equal("slides-agents", d.TalkId));
        Assert.Null(orphan.TalkId);
        Assert.Equal(["2026-03-10-ndc-oslo", "2025-11-18-dotnet-conf-il"], talk.Submissions.Select(s => s.Key)); // newest first
        Assert.Equal("slides-agents-ndc", talk.Submissions[1].DeckSlug);          // entry file name in the talk folder
        Assert.Equal("slides-agents-lightning", talk.Submissions[0].DeckSlug);    // slug of a joined deck
        Assert.Equal("Short.", talk.ShortAbstract);
        Assert.Contains("slides-agents", result.TalksAdded);

        var jedi = (await talks.GetAsync("slides-debugging-jedi"))!;
        Assert.Equal("retired", jedi.Status);
        Assert.Equal(["slides-jedi"], jedi.DeckSlugs);
        Assert.Equal("slides-jedi", jedi.Submissions[0].DeckSlug);
        Assert.Equal("slides-debugging-jedi", all.Single(d => d.Slug == "slides-jedi").TalkId);
        Assert.Equal("Moaid", (await talks.GetSpeakerAsync(source.Id))!.Name);

        // A live session Podium linked to a submission survives a re-sync that edits the submission file.
        await talks.UpsertAsync(talk with { Submissions = talk.Submissions.Select(s => s.Key == "2025-11-18-dotnet-conf-il" ? s with { SessionId = "sess1", SessionDeckSlug = "slides-agents-ndc" } : s).ToList() });
        repo.Sha = "bbbbbbb0000000000000000000000000000000002";
        repo.Changed.Add($"{home}/submissions/2025-11-18-dotnet-conf-il.md");
        repo.Files[$"{home}/submissions/2025-11-18-dotnet-conf-il.md"] = "---\nevent: .NET Conf Israel 2025 (edited)\nstatus: delivered\ndeck: slides.ndc.md\nrecording: https://youtu.be/x\n---\n";
        await sync.SyncAsync(source);
        talk = (await talks.GetAsync("slides-agents"))!;
        var edited = talk.Submissions.Single(s => s.Key == "2025-11-18-dotnet-conf-il");
        Assert.Equal(".NET Conf Israel 2025 (edited)", edited.Event);
        Assert.Equal("sess1", edited.SessionId);
        Assert.Equal("https://youtu.be/x", edited.Recording);

        // The talk folder disappears: the talk is archived, the decks lose their talk.
        repo.Sha = "ccccccc0000000000000000000000000000000003";
        repo.Changed.Clear(); repo.Changed.AddRange([$"{home}/abstract.md", "meetups/agents-lightning/.podium.yml"]);
        repo.Tree.RemoveAll(p => p.StartsWith($"{home}/abstract.md", StringComparison.Ordinal) || p.Contains("/submissions/2025-11", StringComparison.Ordinal) || p.Contains("/submissions/2026-03", StringComparison.Ordinal));
        await sync.SyncAsync(source);
        Assert.True((await talks.GetAsync("slides-agents"))!.Archived);
        Assert.Null((await decks.GetAsync("slides-agents"))!.TalkId);
        Assert.DoesNotContain(await talks.ListAsync(), t => t.Id == "slides-agents");
        Assert.Null((await decks.GetAsync("slides-agents-lightning"))!.TalkId); // talk: now points at nothing (warned, deck stays on its own)
    }

    [Fact]
    public async Task Entry_override_names_the_main_presenterm_file_and_the_plain_slug_follows_it()
    {
        var sources = new InMemorySourceStore();
        var decks = new InMemoryDeckStore();
        var repo = new FakeRepo();
        var buildService = new BuildService(new InMemoryBuildStore(), decks, new FakeArtifacts(), repo, new FakeRunner(), new FakeTokens(), Options.Create(new BuildOptions { PublicBaseUrl = new Uri("https://x.test") }), NullLogger<BuildService>.Instance, sources);
        var sync = new DeckSyncService(sources, decks, repo, buildService, NullLogger<DeckSyncService>.Instance, new InMemoryTalkStore());
        var source = new Source { Id = "owner/slides", Owner = "owner", Repo = "Slides", Trusted = true, InstallationId = 1 };
        await sources.UpsertAsync(source);
        repo.Tree.AddRange(["t/config.yaml", "t/2025_07_30.md", "t/2025_11_18.md"]);
        repo.Files["t/2025_07_30.md"] = "---\ntitle: July\n---\n";
        repo.Files["t/2025_11_18.md"] = "---\ntitle: November\n---\n";
        await sync.SyncAsync(source);
        Assert.Equal("2025_11_18.md", (await decks.GetAsync("slides-t"))!.Entry);
        Assert.Equal("2025_07_30.md", (await decks.GetAsync("slides-t-2025-07-30"))!.Entry);

        // The owner pins the July file as the main one: the plain slug now serves July; November becomes the variant.
        repo.Sha = "bbbbbbb0000000000000000000000000000000002";
        repo.Tree.Add("t/.podium.yml");
        repo.Files["t/.podium.yml"] = "entry: 2025_07_30.md\n";
        repo.Changed.Add("t/.podium.yml");
        await sync.SyncAsync(source);
        Assert.Equal("2025_07_30.md", (await decks.GetAsync("slides-t"))!.Entry);
        var variant = (await decks.ListAsync()).Single(d => d.Slug == "slides-t-2025-11-18");
        Assert.Equal("2025_11_18.md", variant.Entry);
        Assert.True((await decks.GetAsync("slides-t-2025-07-30"))!.Archived); // the old variant record is gone
    }
}
