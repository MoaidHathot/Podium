using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Core.Security;
using Podium.Web.Security;

namespace Podium.Web.Pages;

/// <summary>Guest landing page: the decks explicitly shared with the signed-in GitHub user, plus public decks.</summary>
[Authorize]
public sealed class SharedModel(IDeckStore decks, IGrantStore grants, CallerResolver callers) : PageModel
{
    public IReadOnlyList<(Deck Deck, Grant? Grant)> Decks { get; private set; } = [];
    public Caller Caller { get; private set; } = Caller.Anonymous;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Caller = callers.Resolve(User);
        if (Caller.IsOwner) return Redirect("/");
        var all = await decks.ListAsync(includeArchived: false, ct);
        var list = new List<(Deck, Grant?)>();
        foreach (var d in all)
        {
            if (d.Visibility == Visibility.Public || d.PdfVisibility == Visibility.Public || d.PptxVisibility == Visibility.Public)
            {
                list.Add((d, null));
                continue;
            }
            if (d.Visibility == Visibility.Shared || d.PdfVisibility == Visibility.Shared || d.PptxVisibility == Visibility.Shared)
            {
                var g = (await grants.ListForDeckAsync(d.Slug, ct)).FirstOrDefault(x => x.Principal == Caller.Principal);
                if (g is not null) list.Add((d, g));
            }
        }
        Decks = list.OrderByDescending(x => x.Item1.SavedAt).ToList();
        return Page();
    }
}
