using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Web.Configuration;

namespace Podium.Web.Pages;

public sealed class SourcesModel(ISourceStore sources, IDeckStore decks, IOptions<GitHubOptions> gh, IOptions<PodiumOptions> podium) : PageModel
{
    public IReadOnlyList<(Source Source, int DeckCount, int Archived)> Sources { get; private set; } = [];
    public string? AppSlug => gh.Value.AppSlug;
    public bool AppConfigured => gh.Value.AppConfigured;
    public bool DevToken => !string.IsNullOrWhiteSpace(gh.Value.Token);
    public bool AllowExternal => podium.Value.AllowExternalSources;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var all = await decks.ListAsync(includeArchived: true, ct);
        Sources = (await sources.ListAsync(ct))
            .Select(s => (s, all.Count(d => d.SourceId == s.Id && !d.Archived), all.Count(d => d.SourceId == s.Id && d.Archived)))
            .ToList();
    }
}
