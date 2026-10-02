using Octokit;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Web.GitHub;

public sealed class GitHubRepositoryClient(GitHubAppAuth auth, ISourceStore sources, ILogger<GitHubRepositoryClient> log) : IRepositoryClient
{
    private async Task<GitHubClient> ClientFor(Source source, CancellationToken ct)
    {
        if (source.InstallationId is { } id && auth.Options.AppConfigured)
            return await auth.CreateInstallationClientAsync(id, ct);
        var any = (await sources.ListAsync(ct)).FirstOrDefault(s => s.InstallationId is not null)?.InstallationId;
        return await auth.CreatePublicClientAsync(any, ct);
    }

    public async Task<(string Sha, DateTimeOffset CommittedAt)> GetHeadAsync(Source source, CancellationToken ct = default)
    {
        var client = await ClientFor(source, ct);
        var refName = source.Ref;
        if (string.IsNullOrEmpty(refName))
        {
            var repo = await client.Repository.Get(source.Owner, source.Repo).WaitAsync(ct);
            refName = repo.DefaultBranch;
        }
        var branch = await client.Repository.Branch.Get(source.Owner, source.Repo, refName).WaitAsync(ct);
        var commit = await client.Repository.Commit.Get(source.Owner, source.Repo, branch.Commit.Sha).WaitAsync(ct);
        return (branch.Commit.Sha, commit.Commit.Committer.Date);
    }

    public async Task<IReadOnlyList<string>> ListTreeAsync(Source source, string sha, CancellationToken ct = default)
    {
        var client = await ClientFor(source, ct);
        var tree = await client.Git.Tree.GetRecursive(source.Owner, source.Repo, sha).WaitAsync(ct);
        if (tree.Truncated) log.LogWarning("Tree for {Source}@{Sha} was truncated by GitHub; some decks may be missed", source.Id, sha);
        return tree.Tree.Where(t => t.Type == TreeType.Blob).Select(t => t.Path).ToList();
    }

    public async Task<string?> ReadTextFileAsync(Source source, string sha, string path, CancellationToken ct = default)
    {
        var client = await ClientFor(source, ct);
        try
        {
            var bytes = await client.Repository.Content.GetRawContentByRef(source.Owner, source.Repo, path, sha).WaitAsync(ct);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (NotFoundException) { return null; }
    }

    public async Task<IReadOnlyList<string>> DiffPathsAsync(Source source, string fromSha, string toSha, CancellationToken ct = default)
    {
        var client = await ClientFor(source, ct);
        var compare = await client.Repository.Commit.Compare(source.Owner, source.Repo, fromSha, toSha).WaitAsync(ct);
        var paths = new List<string>();
        foreach (var f in compare.Files)
        {
            paths.Add(f.Filename);
            if (!string.IsNullOrEmpty(f.PreviousFileName)) paths.Add(f.PreviousFileName);
        }
        return paths;
    }

    public async Task<(DateTimeOffset CommittedAt, string Sha)?> LastCommitForPathAsync(Source source, string sha, string path, CancellationToken ct = default)
    {
        var client = await ClientFor(source, ct);
        var request = new CommitRequest { Sha = sha };
        if (!string.IsNullOrEmpty(path)) request.Path = path;
        var commits = await client.Repository.Commit.GetAll(source.Owner, source.Repo, request, new ApiOptions { PageSize = 1, PageCount = 1 }).WaitAsync(ct);
        var c = commits.FirstOrDefault();
        return c is null ? null : (c.Commit.Committer.Date, c.Sha);
    }

    public async Task<Uri> GetAuthenticatedCloneUrlAsync(Source source, CancellationToken ct = default)
    {
        string? token = null;
        if (source.InstallationId is { } id && auth.Options.AppConfigured)
            token = await auth.CreateRepositoryScopedTokenAsync(id, source.Repo, ct); // contents:read on this repo only
        else if (source.IsPrivateRepo)
            token = auth.GetDevToken() ?? throw new InvalidOperationException($"No credentials available to clone private repository {source.FullName}.");

        return token is null
            ? new Uri($"https://github.com/{source.Owner}/{source.Repo}.git")
            : new Uri($"https://x-access-token:{Uri.EscapeDataString(token)}@github.com/{source.Owner}/{source.Repo}.git");
    }

    public async Task<(bool IsPrivate, string DefaultBranch, bool CallerIsOwner, long RepoId)> GetRepoInfoAsync(string owner, string repo, CancellationToken ct = default)
    {
        var any = (await sources.ListAsync(ct)).FirstOrDefault(s => s.InstallationId is not null)?.InstallationId;
        var client = await auth.CreatePublicClientAsync(any, ct);
        var r = await client.Repository.Get(owner, repo).WaitAsync(ct);
        return (r.Private, r.DefaultBranch, r.Permissions?.Admin ?? false, r.Id);
    }
}
