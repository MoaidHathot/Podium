using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Core.Services;
using Podium.Web.Configuration;

namespace Podium.Web.GitHub;

public sealed class GitHubWebhookHandler(
    IOptions<GitHubOptions> options,
    ISourceStore sources,
    SyncQueue queue,
    InstallationDiscovery discovery,
    IHostApplicationLifetime lifetime,
    IServiceScopeFactory scopes,
    GitHubAppAuth auth,
    ILogger<GitHubWebhookHandler> log)
{
    /// <summary>
    /// Processes a verified delivery after the HTTP response has been sent. Bounded by a timeout and by host shutdown;
    /// a failure here is logged and otherwise harmless because the periodic poll catches up on anything missed.
    /// </summary>
    public void HandleInBackground(string eventName, byte[] payload)
    {
        _ = Task.Run(async () =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var outcome = await HandleAsync(eventName, doc, cts.Token);
                log.LogInformation("Webhook {Event}: {Outcome}", eventName, outcome);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Webhook {Event} failed", eventName);
            }
        });
    }

    public bool VerifySignature(ReadOnlySpan<byte> body, string? signatureHeader)
    {
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader)) return false;
        if (!signatureHeader.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        byte[] provided;
        try { provided = Convert.FromHexString(signatureHeader.AsSpan(7)); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    public async Task<string> HandleAsync(string eventName, JsonDocument payload, CancellationToken ct)
    {
        var root = payload.RootElement;
        switch (eventName)
        {
            case "ping":
                return "pong";

            case "push":
                return await HandlePushAsync(root, ct);

            case "installation":
            case "installation_repositories":
                // Re-enumerate everything; cheap at our scale and avoids trusting the payload's repo list.
                var added = await discovery.DiscoverAsync(ct);
                return $"installations refreshed ({added} new sources)";

            case "repository":
                if (root.TryGetProperty("action", out var action) && action.GetString() is "renamed" or "deleted" or "transferred" or "privatized" or "publicized")
                {
                    await discovery.DiscoverAsync(ct);
                    return "repository change processed";
                }
                return "ignored";

            case "check_run":
                return await HandleCheckRunAsync(root, ct);

            default:
                return "ignored";
        }
    }

    private async Task<string> HandlePushAsync(JsonElement root, CancellationToken ct)
    {
        var owner = root.GetProperty("repository").GetProperty("owner").GetProperty("login").GetString() ?? "";
        var repo = root.GetProperty("repository").GetProperty("name").GetString() ?? "";
        var defaultBranch = root.GetProperty("repository").GetProperty("default_branch").GetString() ?? "main";
        var refName = root.GetProperty("ref").GetString() ?? "";
        var id = Source.MakeId(owner, repo);
        long? repoId = root.GetProperty("repository").TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.Number ? rid.GetInt64() : null;

        var source = await sources.GetAsync(id, ct) ?? await ByRepoIdAsync(repoId, owner, repo, ct);
        if (source is null)
        {
            log.LogInformation("Push for unregistered repository {Repo}; running discovery", id);
            await discovery.DiscoverAsync(ct);
            source = await sources.GetAsync(id, ct) ?? await ByRepoIdAsync(repoId, owner, repo, ct);
            if (source is null) return "unknown repository";
        }

        var tracked = "refs/heads/" + (source.Ref ?? defaultBranch);
        if (!string.Equals(refName, tracked, StringComparison.Ordinal)) return $"ignored ref {refName}";

        IReadOnlyCollection<string>? changed = null;
        if (root.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Array)
        {
            // GitHub includes at most 20 commits in the payload; beyond that fall back to a server-side diff.
            var count = commits.GetArrayLength();
            var forced = root.TryGetProperty("forced", out var f) && f.ValueKind == JsonValueKind.True;
            if (count is > 0 and < 20 && !forced)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in commits.EnumerateArray())
                {
                    foreach (var prop in new[] { "added", "modified", "removed" })
                    {
                        if (c.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                            foreach (var p in arr.EnumerateArray()) if (p.GetString() is { } s) set.Add(s);
                    }
                }
                changed = set;
            }
        }

        queue.TryEnqueue(new SyncJob(source.Id, changed, false, "push"));
        return changed is null ? "queued full sync" : $"queued sync of {changed.Count} changed paths";
    }

    /// <summary>
    /// GitHub's "Re-run" button on a Podium check run rebuilds the deck. Only the <c>rerequested</c> action matters:
    /// <c>created</c>/<c>completed</c> are echoes of the runs Podium itself writes. The check run is traced back to the
    /// deck through the external id Podium stored on it, and must belong to the deck's own repository; nothing in the
    /// payload beyond that is trusted (the installation id comes from the source record, not from GitHub's JSON).
    /// </summary>
    private async Task<string> HandleCheckRunAsync(JsonElement root, CancellationToken ct)
    {
        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
        if (action != "rerequested") return $"ignored check_run.{action}";
        if (!root.TryGetProperty("check_run", out var run)) return "ignored (no check_run)";
        if (run.TryGetProperty("app", out var app) && app.TryGetProperty("id", out var appId) && appId.ValueKind == JsonValueKind.Number
            && options.Value.AppId is { } ours && appId.GetInt64() != ours)
            return "ignored (another app's check run)";

        var reference = GitHubChecksObserver.ParseExternalId(run.TryGetProperty("external_id", out var ext) ? ext.GetString() : null);
        if (reference is null) return "ignored (not a Podium check run)";
        var (slug, _) = reference.Value;

        using var scope = scopes.CreateScope();
        var decks = scope.ServiceProvider.GetRequiredService<IDeckStore>();
        var deck = await decks.GetAsync(slug, ct);
        if (deck is null || deck.Archived) return $"ignored (deck {slug} unknown or archived)";
        var source = await sources.GetAsync(deck.SourceId, ct);
        if (source is null) return $"ignored (source {deck.SourceId} unknown)";

        var repository = root.GetProperty("repository");
        long? repoId = repository.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.Number ? rid.GetInt64() : null;
        var fullName = repository.TryGetProperty("full_name", out var fn) ? fn.GetString() : null;
        var sameRepo = source.RepoId is { } knownId && repoId is { } id ? knownId == id : string.Equals(fullName, source.FullName, StringComparison.OrdinalIgnoreCase);
        if (!sameRepo) return $"ignored (check run belongs to {fullName}, deck to {source.FullName})";

        // Re-run means "build this deck again": always from the deck's current commit. A deck is served from its
        // newest successful build, so rebuilding an older commit could silently roll the live deck back.
        var sha = deck.LastCommitSha ?? source.LastSeenSha;
        if (sha is null) return $"ignored (deck {slug} has no commit yet)";
        var build = await scope.ServiceProvider.GetRequiredService<BuildService>().QueueAsync(deck, source, sha, "check-rerun", [], ct, supersedeActive: true);

        // If the re-run was requested on an older commit, GitHub has just reset that run to "queued"; nothing will
        // ever complete it there, so close it with a pointer to where the rebuild went.
        var headSha = run.TryGetProperty("head_sha", out var hs) ? hs.GetString() : null;
        if (headSha is not null && !string.Equals(headSha, sha, StringComparison.OrdinalIgnoreCase) && source.InstallationId is { } installation
            && run.TryGetProperty("id", out var runIdEl) && runIdEl.ValueKind == JsonValueKind.Number)
        {
            try
            {
                var client = await auth.CreateInstallationClientAsync(installation, ct);
                await client.Check.Run.Update(source.Owner, source.Repo, runIdEl.GetInt64(), new Octokit.CheckRunUpdate
                {
                    Status = Octokit.CheckStatus.Completed,
                    Conclusion = Octokit.CheckConclusion.Neutral,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Output = new Octokit.NewCheckRunOutput("Superseded", $"This commit is no longer the deck's latest; the deck was rebuilt from {sha[..7]} instead (build {build.Id})."),
                }).WaitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not close superseded check run for {Deck}", slug); }
        }
        return $"re-run of {slug}: queued build {build.Id} at {sha[..7]}";
    }

    /// <summary>A push from a renamed/transferred repository: find the source by GitHub's id and relabel it.</summary>
    private async Task<Source?> ByRepoIdAsync(long? repoId, string owner, string repo, CancellationToken ct)
    {
        if (repoId is null) return null;
        var match = (await sources.ListAsync(ct)).FirstOrDefault(s => s.RepoId == repoId);
        if (match is null) return null;
        log.LogInformation("Repository {Old} is now {New}; keeping source {Source}", match.FullName, $"{owner}/{repo}", match.Id);
        match = match with { Owner = owner, Repo = repo };
        await sources.UpsertAsync(match, ct);
        return match;
    }
}
