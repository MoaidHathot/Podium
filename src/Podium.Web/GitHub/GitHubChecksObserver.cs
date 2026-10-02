using Microsoft.Extensions.Options;
using Octokit;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Web.Configuration;

namespace Podium.Web.GitHub;

/// <summary>
/// Mirrors builds as GitHub check runs on the commit ("Podium / deck title"), so a broken deck shows up where it
/// was pushed. Requires the App's "Checks: read &amp; write" permission; without it GitHub answers 403 and this
/// observer disables itself for the process lifetime (logged once).
/// </summary>
public sealed class GitHubChecksObserver(GitHubAppAuth auth, IOptions<PodiumOptions> options, ILogger<GitHubChecksObserver> log) : IBuildObserver
{
    private volatile bool _disabled;

    public async Task<string?> OnStartedAsync(Build build, Deck deck, Source source, CancellationToken ct = default)
    {
        if (_disabled || source.InstallationId is not { } installation || !auth.Options.AppConfigured) return null;
        try
        {
            var client = await auth.CreateInstallationClientAsync(installation, ct);
            var run = await client.Check.Run.Create(source.Owner, source.Repo, new NewCheckRun(Name(deck), build.Sha)
            {
                Status = CheckStatus.InProgress,
                StartedAt = DateTimeOffset.UtcNow,
                DetailsUrl = DetailsUrl(deck),
                ExternalId = build.Id,
            }).WaitAsync(ct);
            return run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ForbiddenException ex)
        {
            Disable(ex);
            return null;
        }
    }

    public async Task OnFinishedAsync(Build build, Deck deck, Source source, CancellationToken ct = default)
    {
        if (_disabled || source.InstallationId is not { } installation || !auth.Options.AppConfigured) return;
        try
        {
            var client = await auth.CreateInstallationClientAsync(installation, ct);
            var ok = build.Status == BuildStatus.Succeeded;
            var summary = ok
                ? $"Built in {Duration(build)}. {Artifacts(build)}\n\n[Open deck]({options.Value.PublicBaseUrl.ToString().TrimEnd('/')}/d/{deck.Slug}/)"
                : $"**{build.Error ?? "Build failed"}**\n\n[Build log]({DetailsUrl(deck)})";
            if (build.Warnings.Count > 0) summary += "\n\nWarnings:\n" + string.Join("\n", build.Warnings.Select(w => "- " + w));
            var output = new NewCheckRunOutput(ok ? "Deck built" : "Deck build failed", summary);

            if (build.ExternalRef is { } reference && long.TryParse(reference, out var runId))
            {
                await client.Check.Run.Update(source.Owner, source.Repo, runId, new CheckRunUpdate
                {
                    Status = CheckStatus.Completed,
                    Conclusion = ok ? CheckConclusion.Success : CheckConclusion.Failure,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Output = output,
                    DetailsUrl = DetailsUrl(deck),
                }).WaitAsync(ct);
            }
            else
            {
                await client.Check.Run.Create(source.Owner, source.Repo, new NewCheckRun(Name(deck), build.Sha)
                {
                    Status = CheckStatus.Completed,
                    Conclusion = ok ? CheckConclusion.Success : CheckConclusion.Failure,
                    StartedAt = build.StartedAt ?? build.QueuedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Output = output,
                    DetailsUrl = DetailsUrl(deck),
                    ExternalId = build.Id,
                }).WaitAsync(ct);
            }
        }
        catch (ForbiddenException ex) { Disable(ex); }
    }

    private void Disable(Exception ex)
    {
        if (_disabled) return;
        _disabled = true;
        log.LogWarning("GitHub check runs disabled: the App lacks the 'Checks: read & write' permission ({Message}). Grant it under the App's settings and accept it on the installation, then restart.", ex.Message);
    }

    private static string Name(Deck deck) => $"Podium / {Truncate(deck.Title, 60)}";
    private string DetailsUrl(Deck deck) => $"{options.Value.PublicBaseUrl.ToString().TrimEnd('/')}/decks/{deck.Slug}";
    private static string Duration(Build b) => b.StartedAt is { } s && b.FinishedAt is { } f ? $"{(int)(f - s).TotalMinutes}m {(f - s).Seconds:00}s" : "n/a";
    private static string Artifacts(Build b) => "Artifacts: " + string.Join(", ", new[] { b.HasSite ? "site" : null, b.HasPdf ? "PDF" : null, b.HasPptx ? "PPTX" : null, b.HasPublicSite ? "notes-free site" : null }.Where(x => x is not null));
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
