using Microsoft.AspNetCore.StaticFiles;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Web.Storage;

/// <summary>
/// Development-only artifact store backed by the local filesystem (Storage:ConnectionString = "memory").
/// The builder receives a file:// URL and copies its output there. Mirrors the per-build container layout.
/// </summary>
public sealed class LocalArtifactStore(IConfiguration config, IHostEnvironment env) : IArtifactStore
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private readonly string _root = Path.GetFullPath(config["Storage:LocalPath"] ?? Path.Combine(Path.GetTempPath(), "podium-artifacts"));

    private string BuildDir(string slug, string buildId)
    {
        if (!env.IsDevelopment()) throw new InvalidOperationException("LocalArtifactStore is only allowed in Development.");
        return Path.Combine(_root, BlobArtifactStore.ContainerName(slug, buildId));
    }

    public Task<ArtifactObject?> OpenSiteFileAsync(string deckSlug, string buildId, string relativePath, CancellationToken ct = default)
        => Open(Path.Combine(BuildDir(deckSlug, buildId), "site", relativePath.Replace('/', Path.DirectorySeparatorChar)), BuildDir(deckSlug, buildId));

    public Task<ArtifactObject?> OpenArtifactAsync(string deckSlug, string buildId, ArtifactKind kind, CancellationToken ct = default)
    {
        var name = kind switch { ArtifactKind.Pdf => "deck.pdf", ArtifactKind.Pptx => "deck.pptx", ArtifactKind.Log => "build.log", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
        return Open(Path.Combine(BuildDir(deckSlug, buildId), name), BuildDir(deckSlug, buildId));
    }

    private static Task<ArtifactObject?> Open(string path, string mustBeUnder)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(mustBeUnder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return Task.FromResult<ArtifactObject?>(null);
        if (!File.Exists(full)) return Task.FromResult<ArtifactObject?>(null);
        var info = new FileInfo(full);
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        var contentType = ContentTypes.TryGetContentType(full, out var t) ? t : "application/octet-stream";
        var etag = $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"";
        return Task.FromResult<ArtifactObject?>(new ArtifactObject(stream, contentType, info.Length, etag, info.LastWriteTimeUtc));
    }

    public Task<Uri> CreateUploadUriAsync(string deckSlug, string buildId, TimeSpan lifetime, CancellationToken ct = default)
    {
        var dir = BuildDir(deckSlug, buildId);
        Directory.CreateDirectory(dir);
        return Task.FromResult(new Uri(dir));
    }

    public Task DeleteBuildAsync(string deckSlug, string buildId, CancellationToken ct = default)
    {
        var dir = BuildDir(deckSlug, buildId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }
}
