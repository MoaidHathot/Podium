using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.InMemory;
using Podium.Core.Models;
using Podium.Core.Services;

namespace Podium.Tests;

public class SessionServiceTests
{
    private readonly InMemorySourceStore _sources = new();
    private readonly InMemoryDeckStore _decks = new();
    private readonly InMemoryBuildStore _builds = new();
    private readonly InMemoryShareLinkStore _links = new();
    private readonly InMemorySessionStore _sessions = new();
    private readonly SessionRecorders _recorders;
    private readonly SessionService _svc;
    private readonly LiveSessionOptions _opts = new();

    public SessionServiceTests()
    {
        var buildService = new BuildService(_builds, _decks, new FakeArtifacts(), new FakeRepo(), new FakeRunner(), new FakeTokens(),
            Options.Create(new BuildOptions { PublicBaseUrl = new Uri("https://x.test") }), NullLogger<BuildService>.Instance, _sources);
        _recorders = new SessionRecorders(_sessions, _decks);
        _svc = new SessionService(_sessions, _decks, _links, buildService, _recorders, Options.Create(_opts), NullLogger<SessionService>.Instance);
    }

    private async Task<Deck> SeedAsync(string slug = "talk")
    {
        var source = new Source { Id = "o/r", Owner = "o", Repo = "r", Trusted = true };
        await _sources.UpsertAsync(source);
        var deck = new Deck { Slug = slug, SourceId = source.Id, Path = slug, Entry = "slides.md", Kind = DeckKind.Slidev, CurrentBuildId = "b1", LatestSuccessfulBuildId = "b1" };
        await _decks.UpsertAsync(deck);
        await _builds.UpsertAsync(new Build { Id = "b1", DeckSlug = slug, Sha = "abc", Status = BuildStatus.Succeeded, HasSite = true });
        return deck;
    }

    [Fact]
    public async Task Start_freezes_mints_a_join_link_and_end_revokes_it_with_a_recap()
    {
        await SeedAsync();
        var (session, error) = await _svc.StartAsync("talk", plannedMinutes: 30, holdDeploys: true, freeze: true, title: " My talk ");
        Assert.Null(error);
        Assert.NotNull(session);
        Assert.Equal("My talk", session!.Title);
        var deck = (await _decks.GetAsync("talk"))!;
        Assert.Equal(session.Id, deck.LiveSessionId);
        Assert.Equal("b1", deck.PinnedBuildId); // frozen
        var link = (await _links.GetAsync(session.LinkId!))!;
        Assert.False(link.Revoked);
        Assert.Equal(session.Id, link.SessionId);

        // Second start is refused while live.
        var (dup, dupError) = await _svc.StartAsync("talk", null, false, true, null);
        Assert.Null(dup);
        Assert.Contains("already live", dupError);

        // Presenter moves through slides; pacing is recorded.
        var t0 = DateTimeOffset.UtcNow;
        await _recorders.RecordPositionAsync("talk", 1, 0, t0);
        await _recorders.RecordPositionAsync("talk", 2, 0, t0.AddSeconds(30));
        await _recorders.RecordPositionAsync("talk", 3, 0, t0.AddSeconds(90));
        await _recorders.RecordPositionAsync("talk", 2, 0, t0.AddSeconds(100)); // back to 2
        await _recorders.RecordPositionAsync("talk", 3, 0, t0.AddSeconds(110));
        await _recorders.RecordPresenceAsync("talk", 1, 12);
        await _recorders.RecordPresenceAsync("talk", 1, 7);

        var ended = (await _svc.EndAsync("talk", "manual", unfreeze: true))!;
        Assert.NotNull(ended.EndedAt);
        Assert.Equal("manual", ended.EndReason);
        var recap = ended.Recap!;
        Assert.Equal(12, recap.PeakViewers);
        Assert.Equal(3, recap.SlidesVisited);
        Assert.Equal(30, recap.SecondsPerSlide[1]);
        Assert.Equal(60 + 10, recap.SecondsPerSlide[2]);
        Assert.Equal(3, recap.LastSlide);
        Assert.True((await _links.GetAsync(session.LinkId!))!.Revoked);
        deck = (await _decks.GetAsync("talk"))!;
        Assert.Null(deck.LiveSessionId);
        Assert.Null(deck.PinnedBuildId); // unfrozen on request
        Assert.Null(await _svc.EndAsync("talk", "manual", false)); // nothing live now
    }

    [Fact]
    public async Task Start_does_not_refreeze_an_already_frozen_deck_and_end_leaves_it_frozen()
    {
        var deck = await SeedAsync();
        await _decks.UpsertAsync(deck with { PinnedBuildId = "b1" });
        var (session, _) = await _svc.StartAsync("talk", null, false, true, null);
        Assert.False(session!.FrozeDeck);
        await _svc.EndAsync("talk", "manual", unfreeze: true);
        Assert.Equal("b1", (await _decks.GetAsync("talk"))!.PinnedBuildId);
    }

    [Fact]
    public async Task Sweep_ends_idle_overtime_and_capped_sessions_only()
    {
        await SeedAsync("idle"); await SeedAsync("over"); await SeedAsync("fine"); await SeedAsync("capped");
        _opts.IdleTimeout = TimeSpan.FromMinutes(15);
        _opts.OvertimeGrace = TimeSpan.FromMinutes(30);
        _opts.HardCap = TimeSpan.FromHours(4);

        var (idle, _) = await _svc.StartAsync("idle", null, true, false, null);
        var (over, _) = await _svc.StartAsync("over", 10, false, false, null);
        var (fine, _) = await _svc.StartAsync("fine", 60, false, false, null);
        var (capped, _) = await _svc.StartAsync("capped", null, false, false, null);
        await _sessions.UpsertAsync(idle! with { LastPresenterSeenAt = DateTimeOffset.UtcNow.AddMinutes(-16) });
        await _sessions.UpsertAsync(over! with { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-41), LastPresenterSeenAt = DateTimeOffset.UtcNow });
        await _sessions.UpsertAsync(capped! with { StartedAt = DateTimeOffset.UtcNow.AddHours(-5), LastPresenterSeenAt = DateTimeOffset.UtcNow });

        // "fine" and "over" still have a presenter connected; "idle" and "capped" do not.
        var ended = await _svc.SweepAsync(slug => slug is "fine" or "over" or "capped" ? 1 : 0);
        Assert.Equal(3, ended);
        Assert.Equal("idle", (await _sessions.GetAsync("idle", idle.Id))!.EndReason);
        Assert.Equal("overtime", (await _sessions.GetAsync("over", over.Id))!.EndReason);
        Assert.Equal("cap", (await _sessions.GetAsync("capped", capped.Id))!.EndReason);
        Assert.Null((await _sessions.GetAsync("fine", fine!.Id))!.EndedAt);
        // The idle one asked to hold deployments; after the sweep nothing holds them any more.
        Assert.Empty(await _svc.HoldingDeploysAsync());
    }

    [Fact]
    public async Task Join_codes_are_unambiguous_resolve_only_while_live_and_plans_can_be_adjusted()
    {
        await SeedAsync();
        var (session, _) = await _svc.StartAsync("talk", 45, false, true, null);
        var code = session!.JoinCode!;
        Assert.Equal(6, code.Length);
        Assert.All(code, ch => Assert.Contains(ch, "ABCDEFGHJKMNPQRSTUVWXYZ23456789"));
        Assert.Equal($"{code[..3]}-{code[3..]}", SessionService.FormatJoinCode(code));
        Assert.Equal("https://slides.example/j/" + SessionService.FormatJoinCode(code), SessionService.JoinUrl(new Uri("https://slides.example/"), code));

        // Typed by the room: dashes, spaces and case do not matter.
        Assert.Equal(session.Id, (await _svc.FindLiveByJoinCodeAsync($" {code[..3].ToLowerInvariant()}-{code[3..]} "))!.Id);
        Assert.Null(await _svc.FindLiveByJoinCodeAsync("ZZZZZZ"));
        Assert.Null(SessionService.NormalizeJoinCode("ABC-10X")); // 0/1/I/L/O are never issued, so never accepted
        Assert.Null(SessionService.NormalizeJoinCode("ABCDE"));

        var updated = await _svc.UpdatePlanAsync("talk", 40);
        Assert.Equal(40, updated!.PlannedMinutes);
        Assert.Null(await _svc.UpdatePlanAsync("talk", 0));
        Assert.Null(await _svc.UpdatePlanAsync("talk", 601));

        await _svc.EndAsync("talk", "manual", false);
        Assert.Null(await _svc.FindLiveByJoinCodeAsync(code)); // dead with the session
        Assert.Null(await _svc.UpdatePlanAsync("talk", 30));
    }

    [Fact]
    public async Task Holding_deploys_reflects_only_live_opted_in_sessions()
    {
        await SeedAsync("a"); await SeedAsync("b"); await SeedAsync("c");
        await _svc.StartAsync("a", null, holdDeploys: true, true, null);
        await _svc.StartAsync("b", null, holdDeploys: false, true, null);
        await _svc.StartAsync("c", null, holdDeploys: true, true, null);
        Assert.Equal(2, (await _svc.HoldingDeploysAsync()).Count);
        await _svc.EndAsync("a", "manual", false);
        Assert.Single(await _svc.HoldingDeploysAsync());
        Assert.Equal("c", (await _svc.HoldingDeploysAsync())[0].DeckSlug);
    }

    [Fact]
    public async Task Ended_sessions_attach_to_the_matching_submission_of_the_deck_s_talk()
    {
        var talks = new InMemoryTalkStore();
        var buildService = new BuildService(_builds, _decks, new FakeArtifacts(), new FakeRepo(), new FakeRunner(), new FakeTokens(),
            Options.Create(new BuildOptions { PublicBaseUrl = new Uri("https://x.test") }), NullLogger<BuildService>.Instance, _sources);
        var svc = new SessionService(_sessions, _decks, _links, buildService, _recorders, Options.Create(_opts), NullLogger<SessionService>.Instance, talks);
        var deck = await SeedAsync("agents");
        await _decks.UpsertAsync(deck with { TalkId = "o-r-agents" });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await talks.UpsertAsync(new Talk
        {
            Id = "o-r-agents", LocalId = "agents", SourceId = "o/r", Path = "agents", Title = "Agents",
            Submissions =
            [
                new Submission { Key = "old", Event = "Last year", Date = today.AddDays(-300), Status = "delivered" },
                new Submission { Key = "other-deck", Event = "Workshop", Date = today, Status = "accepted", DeckSlug = "agents-workshop" },
                new Submission { Key = "declined", Event = "Nope", Date = today, Status = "declined" },
                new Submission { Key = "today", Event = "Conf", Date = today.AddDays(1), Status = "accepted" },
            ],
        });

        // Too short to be a delivery: nothing links.
        var (s1, _) = await svc.StartAsync("agents", 45, false, false, null);
        var short1 = (await svc.EndAsync("agents", "manual", false))!;
        Assert.Null((await talks.GetAsync("o-r-agents"))!.Submissions.First(s => s.Key == "today").SessionId);

        // A real delivery links to the accepted submission around the date, skipping the one for the other deck and the declined one.
        var (s2, _) = await svc.StartAsync("agents", 45, false, false, null);
        await _sessions.UpsertAsync(s2! with { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-40) });
        var ended = (await svc.EndAsync("agents", "manual", false))!;
        var talk = (await talks.GetAsync("o-r-agents"))!;
        var linked = talk.Submissions.Single(s => s.SessionId is not null);
        Assert.Equal("today", linked.Key);
        Assert.Equal(ended.Id, linked.SessionId);
        Assert.Equal("agents", linked.SessionDeckSlug);

        // A later session on the same day does not steal the link; rehearsals never link.
        var (s3, _) = await svc.StartAsync("agents", 45, false, false, null, rehearsal: true);
        await _sessions.UpsertAsync(s3! with { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-40) });
        await svc.EndAsync("agents", "manual", false);
        Assert.Equal(ended.Id, (await talks.GetAsync("o-r-agents"))!.Submissions.Single(s => s.Key == "today").SessionId);
        Assert.Single((await talks.GetAsync("o-r-agents"))!.Submissions, s => s.SessionId is not null);
    }
}
