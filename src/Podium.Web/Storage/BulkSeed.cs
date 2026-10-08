using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Web.Serving;

namespace Podium.Web.Storage;

/// <summary>
/// Development/CI only: fabricates a library the size of a real one (<c>/dev-seed?bulk=N</c>), so the browser smoke
/// test and local measurements run against a hundred-odd cards rather than two. Decks are metadata plus a
/// thumbnail (no site), spread over talks with two or three variants each, mixed kinds, folders, visibilities and
/// a decade of document dates - the shapes the library's grouping, sorting and densities have to cope with.
/// </summary>
public static class BulkSeed
{
    public const string BuildId = "load0001";
    private static readonly string[] Topics = ["Agents in production", "MCP from scratch", "Minimal APIs, maximal results", "Source generators without tears", "What's new in C#", "Observability for the rest of us", "Testing the untestable", "Cloud-native .NET on a budget"];

    public static async Task SeedAsync(int count, LocalArtifactStore local, IDeckStore decks, IBuildStore builds, ITalkStore talks, DeckAccessService access, CancellationToken ct)
    {
        var talkDecks = new Dictionary<int, List<string>>();
        for (var i = 0; i < count; i++)
        {
            var slug = $"load-deck-{i:000}";
            var inTalk = i % 7 != 6;                       // roughly one deck in seven stands alone
            var talk = i / 3;
            var kind = (i % 5) switch { 3 => DeckKind.Slidev, 4 => DeckKind.Presenterm, _ => i % 2 == 0 ? DeckKind.PowerPoint : DeckKind.Pdf };
            var fileBased = Podium.Core.Discovery.DeckDetector.IsFileBased(kind);
            var authored = new DateTimeOffset(2016, 1, 1, 9, 0, 0, TimeSpan.Zero).AddDays(i * 29);
            var dir = (await local.CreateUploadUriAsync(slug, BuildId, TimeSpan.FromMinutes(5), ct)).LocalPath;
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, "thumbnail.jpg"), FixtureDeck.PageJpeg, ct);
            await decks.UpsertAsync(new Deck
            {
                Slug = slug, SourceId = "fixture/slides",
                Path = inTalk ? $"talks/talk-{talk:00}" : $"courses/course-{i:000}",
                Entry = fileBased ? $"deck-{i:000}.{(kind == DeckKind.Pdf ? "pdf" : "pptx")}" : "slides.md",
                Kind = kind,
                Title = $"{Topics[i % Topics.Length]}{(i % 4 == 0 ? " - the extended edition with a title long enough to wrap on a phone" : "")} ({i:000})",
                Tags = ["load", $"topic-{i % 6}"],
                Variant = inTalk && i % 3 == 1 ? "workshop" : inTalk && i % 3 == 2 ? "lightning" : null,
                TalkId = inTalk ? $"fixture-slides-load-{talk}" : null,
                Visibility = (i % 9) switch { 0 => Visibility.Public, 1 => Visibility.Link, _ => Visibility.Private },
                Pinned = i == 4,
                AuthoredAt = fileBased ? authored : null, DocumentCreatedAt = fileBased ? authored.AddMonths(-2) : null,
                LastCommitAt = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero), LastCommitSha = new string('b', 40),
                CurrentBuildId = BuildId, LatestSuccessfulBuildId = BuildId, LatestBuildId = BuildId,
                LatestBuildStatus = i % 23 == 22 ? BuildStatus.Failed : BuildStatus.Succeeded,
                CurrentHasThumbnail = true, CurrentHasSlideSheet = true, CurrentSlideCount = 12 + i % 30, CurrentHasPdf = fileBased, CurrentHasPptx = kind == DeckKind.PowerPoint,
            }, ct);
            await builds.UpsertAsync(new Build { Id = BuildId, DeckSlug = slug, Sha = new string('b', 40), Status = BuildStatus.Succeeded, HasSite = true, HasThumbnail = true, HasSlideSheet = true, SlideCount = 12 + i % 30, FinishedAt = DateTimeOffset.UtcNow.AddDays(-1), StartedAt = DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(-1) }, ct);
            access.Invalidate(slug);
            if (inTalk) (talkDecks.TryGetValue(talk, out var list) ? list : talkDecks[talk] = []).Add(slug);
        }
        foreach (var (talk, slugs) in talkDecks)
        {
            var given = new DateOnly(2019, 3, 1).AddMonths(talk * 2);
            await talks.UpsertAsync(new Talk
            {
                Id = $"fixture-slides-load-{talk}", LocalId = $"load-{talk}", SourceId = "fixture/slides", Path = $"talks/talk-{talk:00}",
                Title = $"{Topics[talk % Topics.Length]} ({talk:00})", Level = (talk % 3) switch { 0 => "introductory", 1 => "intermediate", _ => "advanced" }, Durations = [45],
                Status = talk % 5 == 4 ? "draft" : talk % 11 == 10 ? "retired" : "available", Public = talk % 4 == 0, Tags = ["load", $"topic-{talk % 6}"],
                Abstract = $"A load-test talk about {Topics[talk % Topics.Length].ToLowerInvariant()}: what it is, why it matters and how to start on Monday.",
                Parts = [new TalkPart("Short abstract", $"Load-test talk {talk:00} in one sentence.")],
                DeckSlugs = slugs, LastCommitAt = DateTimeOffset.UtcNow.AddDays(-talk),
                Submissions =
                [
                    new Submission { Key = $"{given:yyyy-MM-dd}-conf-{talk}", Event = $"Conf {talk:00}", Date = given, Status = "delivered", Format = "talk", Duration = 45, DeckSlug = slugs[0] },
                    new Submission { Key = $"{given.AddYears(1):yyyy-MM-dd}-meetup-{talk}", Event = $"Meetup {talk:00}", Date = given.AddYears(1), Status = talk % 3 == 0 ? "delivered" : "declined", Format = "talk", Duration = 45 },
                ],
            }, ct);
        }
    }
}
