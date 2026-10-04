using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Core.Services;

public sealed record SearchHit(string Slug, string Title, int Slide, string? SlideTitle, string Snippet, int Score);

/// <summary>
/// Full-text search across the owner's decks, over the per-slide text the builder extracts (text.json). Kept in
/// memory (a few MB at most), rebuilt from the served builds at start-up and refreshed whenever a deck's served build
/// changes. Owner-only by construction: the API that exposes it is behind the owner policy.
/// </summary>
public sealed class DeckSearchIndex(IArtifactStore artifacts, ILogger<DeckSearchIndex> log)
{
    private sealed record Page(int Index, string? Title, string Text, string Lower);
    private sealed record Entry(string BuildId, string Title, IReadOnlyList<Page> Pages);

    private readonly ConcurrentDictionary<string, Entry> _decks = new(StringComparer.Ordinal);
    private const int MaxPagesPerDeck = 400;

    public int DeckCount => _decks.Count;

    /// <summary>Loads (or refreshes) a deck's text from its served build; removes it when the build has no text.</summary>
    public async Task RefreshAsync(Deck deck, CancellationToken ct = default)
    {
        if (deck.Archived || deck.CurrentBuildId is null || !deck.CurrentHasText) { _decks.TryRemove(deck.Slug, out _); return; }
        if (_decks.TryGetValue(deck.Slug, out var existing) && existing.BuildId == deck.CurrentBuildId && existing.Title == deck.Title) return;
        try
        {
            await using var file = await artifacts.OpenArtifactAsync(deck.Slug, deck.CurrentBuildId, ArtifactKind.Text, ct);
            if (file is null) { _decks.TryRemove(deck.Slug, out _); return; }
            using var doc = await JsonDocument.ParseAsync(file.Content, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            var pages = new List<Page>();
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                if (pages.Count >= MaxPagesPerDeck) break;
                var index = p.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : pages.Count + 1;
                var title = p.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var text = p.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
                if (text.Length > 20000) text = text[..20000];
                pages.Add(new Page(index, title, text, text.ToLowerInvariant()));
            }
            _decks[deck.Slug] = new Entry(deck.CurrentBuildId, deck.Title, pages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not index text of {Deck}", deck.Slug);
        }
    }

    public void Remove(string slug) => _decks.TryRemove(slug, out _);

    /// <summary>Ranks slides by how many query terms they contain (all terms required), title matches first.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int take = 20)
    {
        var terms = (query ?? "").ToLowerInvariant().Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2).Distinct().Take(8).ToArray();
        if (terms.Length == 0) return [];
        var hits = new List<SearchHit>();
        foreach (var (slug, entry) in _decks)
        {
            foreach (var page in entry.Pages)
            {
                var score = 0;
                var all = true;
                foreach (var term in terms)
                {
                    var idx = page.Lower.IndexOf(term, StringComparison.Ordinal);
                    if (idx < 0) { all = false; break; }
                    score += 1 + (page.Title is not null && page.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ? 3 : 0);
                }
                if (!all) continue;
                hits.Add(new SearchHit(slug, entry.Title, page.Index, page.Title, Snippet(page.Text, page.Lower.IndexOf(terms[0], StringComparison.Ordinal)), score));
            }
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Slide).Take(Math.Clamp(take, 1, 100)).ToList();
    }

    private static string Snippet(string text, int at)
    {
        if (text.Length <= 160) return text;
        var start = Math.Max(0, at - 60);
        var end = Math.Min(text.Length, start + 160);
        var s = text[start..end].Trim();
        return (start > 0 ? "…" : "") + s + (end < text.Length ? "…" : "");
    }
}
