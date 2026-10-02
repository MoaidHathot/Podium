using System.Threading.Channels;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Core.Services;

namespace Podium.Web.GitHub;

public sealed record SyncJob(string SourceId, IReadOnlyCollection<string>? ChangedPaths, bool Force, string TriggeredBy);

/// <summary>
/// Serialises repository syncs through a single background worker so webhook bursts never run concurrent scans of
/// the same source. Bounded so a flood of webhooks cannot exhaust memory.
/// </summary>
public sealed class SyncQueue : BackgroundService
{
    private readonly Channel<SyncJob> _channel = Channel.CreateBounded<SyncJob>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SyncQueue> _log;

    public SyncQueue(IServiceScopeFactory scopes, ILogger<SyncQueue> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public bool TryEnqueue(SyncJob job) => _channel.Writer.TryWrite(job);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
                var sync = scope.ServiceProvider.GetRequiredService<DeckSyncService>();
                var source = await sources.GetAsync(job.SourceId, stoppingToken);
                if (source is null) { _log.LogWarning("Sync requested for unknown source {Source}", job.SourceId); continue; }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromMinutes(10));
                await sync.SyncAsync(source, job.ChangedPaths, job.Force, job.TriggeredBy, cts.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sync of {Source} failed", job.SourceId);
            }
        }
    }
}

/// <summary>Discovers repositories the GitHub App is installed on and registers them as trusted sources.</summary>
public sealed class InstallationDiscovery(GitHubAppAuth auth, ISourceStore sources, SyncQueue queue, ILogger<InstallationDiscovery> log)
{
    public async Task<int> DiscoverAsync(CancellationToken ct)
    {
        if (!auth.Options.AppConfigured) return 0;
        var app = auth.CreateAppClient();
        var installations = await app.GitHubApps.GetAllInstallationsForCurrent().WaitAsync(ct);
        var known = (await sources.ListAsync(ct)).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var knownByRepoId = known.Values.Where(s => s.RepoId is not null).ToDictionary(s => s.RepoId!.Value);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;

        foreach (var inst in installations)
        {
            var client = await auth.CreateInstallationClientAsync(inst.Id, ct);
            var repos = await client.GitHubApps.Installation.GetAllRepositoriesForCurrent().WaitAsync(ct);
            foreach (var r in repos.Repositories)
            {
                var id = Source.MakeId(r.Owner.Login, r.Name);
                // GitHub's numeric id is the identity; the name is a label. A renamed or transferred repository keeps
                // its source (and so its decks, slugs, grants and links) and only has its label updated.
                if (!known.TryGetValue(id, out var existing) && knownByRepoId.TryGetValue(r.Id, out var renamed))
                {
                    log.LogInformation("Repository {Old} is now {New}; keeping source {Source}", renamed.FullName, r.FullName, renamed.Id);
                    existing = renamed with { Owner = r.Owner.Login, Repo = r.Name };
                    await sources.UpsertAsync(existing, ct);
                    known[existing.Id] = existing;
                }
                if (existing is not null)
                {
                    seen.Add(existing.Id);
                    if (existing.InstallationId != inst.Id || existing.IsPrivateRepo != r.Private || !existing.Trusted || existing.RepoId != r.Id)
                        await sources.UpsertAsync(existing with { InstallationId = inst.Id, IsPrivateRepo = r.Private, Trusted = true, RepoId = r.Id }, ct);
                    continue;
                }
                seen.Add(id);
                var source = new Source { Id = id, Owner = r.Owner.Login, Repo = r.Name, RepoId = r.Id, InstallationId = inst.Id, Trusted = true, IsPrivateRepo = r.Private };
                await sources.UpsertAsync(source, ct);
                queue.TryEnqueue(new SyncJob(id, null, false, "installation"));
                added++;
                log.LogInformation("Registered source {Source} from installation {Installation}", id, inst.Id);
            }
        }

        // Sources that lost their installation become external (public only) or are left for the owner to remove.
        foreach (var s in known.Values.Where(s => s.InstallationId is not null && !seen.Contains(s.Id)))
        {
            log.LogWarning("Installation for {Source} no longer grants access; marking as external", s.Id);
            await sources.UpsertAsync(s with { InstallationId = null, Trusted = false }, ct);
        }
        return added;
    }
}
