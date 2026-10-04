using Microsoft.AspNetCore.Mvc.RazorPages;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Web.Pages;

/// <summary>Owner-only (folder policy): the full audit trail, newest first.</summary>
public sealed class ActivityModel(IAuditStore audit) : PageModel
{
    public IReadOnlyList<AuditEntry> Entries { get; private set; } = [];
    public async Task OnGetAsync(CancellationToken ct) => Entries = await audit.RecentAsync(null, 200, ct);
}