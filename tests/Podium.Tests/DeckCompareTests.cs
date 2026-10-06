using Podium.Core.Services;

namespace Podium.Tests;

public sealed class DeckCompareTests
{
    private static SlidePage P(int i, string text, string? title = null) => new(i, title, text);

    [Fact]
    public void Aligns_in_order_and_classifies_rows()
    {
        var left = new[] { P(1, "welcome to the talk about agents"), P(2, "what is an agent anyway a loop with tools"), P(3, "demo time"), P(4, "thank you questions") };
        var right = new[] { P(1, "welcome to the talk about agents"), P(2, "sponsors"), P(3, "what is an agent anyway a loop with tools and memory"), P(4, "thank you questions") };
        var r = DeckCompare.Compare(left, right);

        Assert.Equal(2, r.Same);      // slides 1 and 4
        Assert.Equal(1, r.Changed);
        Assert.Equal(1, r.OnlyLeft);  // demo time
        Assert.Equal(1, r.OnlyRight); // sponsors
        Assert.Equal([CompareKind.Same, CompareKind.OnlyRight, CompareKind.Changed, CompareKind.OnlyLeft, CompareKind.Same], r.Rows.Select(x => x.Kind).ToArray());
        Assert.InRange(r.Similarity, 0.6, 0.95);
        var changed = r.Rows.Single(x => x.Kind == CompareKind.Changed);
        Assert.Equal(2, changed.Left!.Index);
        Assert.Equal(3, changed.Right!.Index);
    }

    [Fact]
    public void Identical_decks_are_fully_similar_and_disjoint_decks_are_not()
    {
        var a = new[] { P(1, "alpha beta"), P(2, "gamma delta") };
        var same = DeckCompare.Compare(a, a);
        Assert.Equal(1, same.Similarity);
        Assert.Equal(2, same.Same);
        var other = new[] { P(1, "one two"), P(2, "three four") };
        var none = DeckCompare.Compare(a, other);
        Assert.Equal(0, none.Similarity);
        Assert.Equal(2, none.OnlyLeft);
        Assert.Equal(2, none.OnlyRight);
        Assert.Equal(1, DeckCompare.Compare([], []).Similarity);
    }

    [Fact]
    public void Word_diff_marks_insertions_and_deletions_ignoring_case_and_punctuation()
    {
        var d = DeckCompare.WordDiff("Agents are loops, with tools.", "agents are Loops with tools and memory");
        Assert.Equal(" agents  are  Loops  with  tools +and +memory", string.Join(' ', d.Select(t => t.Op + t.Text)));
        Assert.DoesNotContain(d, t => t.Op == '-');
        var removed = DeckCompare.WordDiff("a b c", "a c");
        Assert.Equal([' ', '-', ' '], removed.Select(t => t.Op).ToArray());
    }

    [Fact]
    public void Parses_text_json_defensively()
    {
        var pages = DeckCompare.ParseText("[{\"index\":1,\"title\":\"One\",\"text\":\"a\"},{\"text\":\"b\"},{\"index\":3,\"title\":\"  \",\"text\":7}]");
        Assert.Equal(3, pages.Count);
        Assert.Equal("One", pages[0].Title);
        Assert.Equal(2, pages[1].Index);
        Assert.Null(pages[2].Title);
        Assert.Equal("", pages[2].Text);
        Assert.Empty(DeckCompare.ParseText("{}"));
    }
}
