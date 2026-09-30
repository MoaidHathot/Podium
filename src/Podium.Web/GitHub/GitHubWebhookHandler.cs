using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Web.Configuration;

namespace Podium.Web.GitHub;

public sealed class GitHubWebhookHandler(
    IOptions<GitHubOptions> options,
    ISourceStore sources,
    SyncQueue queue,
    InstallationDiscovery discovery,
    ILogger<GitHubWebhookHandler> log)
{
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

        var source = await sources.GetAsync(id, ct);
        if (source is null)
        {
            log.LogInformation("Push for unregistered repository {Repo}; running discovery", id);
            await discovery.DiscoverAsync(ct);
            source = await sources.GetAsync(id, ct);
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

        queue.TryEnqueue(new SyncJob(id, changed, false, "push"));
        return changed is null ? "queued full sync" : $"queued sync of {changed.Count} changed paths";
    }
}
