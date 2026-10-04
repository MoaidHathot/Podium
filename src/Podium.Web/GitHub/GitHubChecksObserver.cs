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
    private volatile bool _confirmed;

    /// <summary>External id stored on the check run so a "Re-run" click can be traced back to the deck: "slug/buildId".</summary>
    public static string ExternalId(Deck deck, Build build) => $"{deck.Slug}/{build.Id}";

    /// <summary>Inverse of <see cref="ExternalId"/>; null for ids Podium did not write.</summary>
    public static (string Slug, string BuildId)? ParseExternalId(string? externalId)
    {
        if (string.IsNullOrEmpty(externalId)) return null;
        var i = externalId.LastIndexOf('/');
        if (i <= 0 || i == externalId.Length - 1) return null;
        var slug = externalId[..i];
        return Podium.Core.Slug.IsValid(slug) ? (slug, externalId[(i + 1)..]) : null;
    }

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
                ExternalId = ExternalId(deck, build),
            }).WaitAsync(ct);
            if (!_confirmed)
            {
                _confirmed = true;
                log.LogInformation("GitHub check runs active (first run {RunId} on {Repo}@{Sha})", run.Id, source.FullName, build.Sha[..7]);
            }
            return run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ApiException ex) when (IsMissingPermission(ex))
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
                    ExternalId = ExternalId(deck, build),
                }).WaitAsync(ct);
            }
        }
        catch (ApiException ex) when (IsMissingPermission(ex)) { Disable(ex); }
    }

    // GitHub answers an installation token that lacks a permission with 403 "Resource not accessible by integration"
    // (a few endpoints use 404 with the same message). Anything else is a per-build problem, not a configuration one.
    private static bool IsMissingPermission(ApiException ex)
        => ex.StatusCode == System.Net.HttpStatusCode.Forbidden
           || (ex.Message?.Contains("not accessible by integration", StringComparison.OrdinalIgnoreCase) ?? false);

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
