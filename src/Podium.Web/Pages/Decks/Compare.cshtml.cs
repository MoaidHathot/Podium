using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Core.Services;

namespace Podium.Web.Pages.Decks;

/// <summary>
/// Side-by-side comparison of two decks (typically two variants of a talk) over their extracted slide text: which
/// slides are identical, which were reworded and which exist on one side only, with thumbnails from the slide sheets.
/// </summary>
public sealed class CompareModel(IDeckStore decks, IArtifactStore artifacts, ITalkStore talks) : PageModel
{
    public Deck Left { get; private set; } = null!;
    public Deck Right { get; private set; } = null!;
    public Talk? Talk { get; private set; }
    public IReadOnlyList<Deck> Others { get; private set; } = [];
    public CompareResult? Result { get; private set; }
    public SheetGeometry? LeftSheet { get; private set; }
    public SheetGeometry? RightSheet { get; private set; }
    public string? Problem { get; private set; }

    public async Task<IActionResult> OnGetAsync(string slug, string other, CancellationToken ct)
    {
        if (slug == other) return Redirect($"/decks/{slug}");
        var left = await decks.GetAsync(slug, ct);
        var right = await decks.GetAsync(other, ct);
        if (left is null || right is null) return NotFound();
        Left = left; Right = right;
        if (left.TalkId is not null && left.TalkId == right.TalkId) Talk = await talks.GetAsync(left.TalkId, ct);
        if (Talk is not null)
        {
            var others = new List<Deck>();
            foreach (var s in Talk.DeckSlugs.Where(s => s != slug))
                if (await decks.GetAsync(s, ct) is { Archived: false, CurrentHasText: true } d) others.Add(d);
            Others = others;
        }

        var leftPages = await LoadTextAsync(left, ct);
        var rightPages = await LoadTextAsync(right, ct);
        if (leftPages is null || rightPages is null)
        {
            Problem = leftPages is null && rightPages is null ? "Neither deck has extracted slide text yet; build both decks first."
                : leftPages is null ? $"“{left.Title}” has no extracted slide text yet (build it first)." : $"“{right.Title}” has no extracted slide text yet (build it first).";
            return Page();
        }
        Result = DeckCompare.Compare(leftPages, rightPages);
        LeftSheet = await LoadSheetAsync(left, ct);
        RightSheet = await LoadSheetAsync(right, ct);
        return Page();
    }

    private async Task<IReadOnlyList<SlidePage>?> LoadTextAsync(Deck deck, CancellationToken ct)
    {
        if (deck.CurrentBuildId is null || !deck.CurrentHasText) return null;
        try
        {
            await using var file = await artifacts.OpenArtifactAsync(deck.Slug, deck.CurrentBuildId, ArtifactKind.Text, ct);
            return file is null ? null : await DeckCompare.ParseTextAsync(file.Content, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private async Task<SheetGeometry?> LoadSheetAsync(Deck deck, CancellationToken ct)
    {
        if (deck.CurrentBuildId is null || !deck.CurrentHasSlideSheet) return null;
        try
        {
            await using var file = await artifacts.OpenArtifactAsync(deck.Slug, deck.CurrentBuildId, ArtifactKind.SlideSheetMeta, ct);
            if (file is null) return null;
            using var doc = await JsonDocument.ParseAsync(file.Content, cancellationToken: ct);
            var r = doc.RootElement;
            int Int(string name) => r.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
            var g = new SheetGeometry(Int("count"), Int("cols"), Int("rows"), Int("cellWidth"), Int("cellHeight"));
            return g.Count > 0 && g.Cols > 0 && g.Rows > 0 && g.CellWidth > 0 && g.CellHeight > 0 ? g : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Inline style showing slide <paramref name="index"/> (1-based) of the sheet as a sprite at the given width.</summary>
    public static string SpriteStyle(Deck deck, SheetGeometry g, int index, int width)
    {
        var i = Math.Clamp(index - 1, 0, g.Count - 1);
        var col = i % g.Cols;
        var row = i / g.Cols;
        var scale = (double)width / g.CellWidth;
        var height = (int)Math.Round(g.CellHeight * scale);
        return $"width:{width}px;height:{height}px;background-image:url('/d/{deck.Slug}/slides.jpg?v={deck.CurrentBuildId}');background-size:{(int)Math.Round(g.Cols * g.CellWidth * scale)}px {(int)Math.Round(g.Rows * g.CellHeight * scale)}px;background-position:-{(int)Math.Round(col * g.CellWidth * scale)}px -{(int)Math.Round(row * g.CellHeight * scale)}px";
    }
}

public sealed record SheetGeometry(int Count, int Cols, int Rows, int CellWidth, int CellHeight);
