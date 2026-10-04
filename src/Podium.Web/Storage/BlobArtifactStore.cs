using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.StaticFiles;
using Podium.Core.Abstractions;
using Podium.Core.Models;

namespace Podium.Web.Storage;

/// <summary>
/// One blob container per build ("b-{buildId}-{hash(slug)}"). The builder receives a SAS scoped to exactly that
/// container, so a compromised build cannot read or modify any other deck. Deleting a build deletes the container.
/// Layout inside: site/** (SPA), deck.pdf, deck.pptx, build.log
/// </summary>
public sealed class BlobArtifactStore(BlobServiceClient service, ILogger<BlobArtifactStore> log) : IArtifactStore
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = CreateContentTypes();

    public static string ContainerName(string deckSlug, string buildId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deckSlug)))[..12].ToLowerInvariant();
        return $"b-{buildId.ToLowerInvariant()}-{hash}";
    }

    public async Task<ArtifactObject?> OpenSiteFileAsync(string deckSlug, string buildId, string relativePath, CancellationToken ct = default, string variant = "site")
        => await OpenAsync(deckSlug, buildId, variant + "/" + relativePath.TrimStart('/'), ct);

    public async Task<ArtifactObject?> OpenArtifactAsync(string deckSlug, string buildId, ArtifactKind kind, CancellationToken ct = default)
    {
        var name = kind switch
        {
            ArtifactKind.Pdf => "deck.pdf",
            ArtifactKind.Pptx => "deck.pptx",
            ArtifactKind.Log => "build.log",
            ArtifactKind.Thumbnail => "thumbnail.jpg",
            ArtifactKind.Notes => "notes.json",
            ArtifactKind.Text => "text.json",
            ArtifactKind.SlideSheet => "slides.jpg",
            ArtifactKind.SlideSheetMeta => "slides.json",
            ArtifactKind.Manifest => "manifest.json",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return await OpenAsync(deckSlug, buildId, name, ct);
    }

    private async Task<ArtifactObject?> OpenAsync(string deckSlug, string buildId, string blobName, CancellationToken ct)
    {
        var blob = service.GetBlobContainerClient(ContainerName(deckSlug, buildId)).GetBlobClient(blobName);
        try
        {
            var props = await blob.GetPropertiesAsync(cancellationToken: ct);
            var stream = await blob.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false) { BufferSize = 256 * 1024 }, ct);
            var contentType = props.Value.ContentType;
            if (string.IsNullOrEmpty(contentType) || contentType == "application/octet-stream")
                contentType = ContentTypes.TryGetContentType(blobName, out var ctFromName) ? ctFromName : "application/octet-stream";
            return new ArtifactObject(stream, contentType, props.Value.ContentLength, props.Value.ETag.ToString(), props.Value.LastModified);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<Uri> CreateUploadUriAsync(string deckSlug, string buildId, TimeSpan lifetime, CancellationToken ct = default)
    {
        var container = service.GetBlobContainerClient(ContainerName(deckSlug, buildId));
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);

        var expires = DateTimeOffset.UtcNow.Add(lifetime);
        var permissions = BlobContainerSasPermissions.Write | BlobContainerSasPermissions.Create | BlobContainerSasPermissions.List | BlobContainerSasPermissions.Read;

        if (container.CanGenerateSasUri)
        {
            return container.GenerateSasUri(permissions, expires);
        }

        // Managed identity: user-delegation SAS (requires "Storage Blob Delegator" + data-plane role on the account).
        var key = await service.GetUserDelegationKeyAsync(DateTimeOffset.UtcNow.AddMinutes(-5), expires, ct);
        var builder = new BlobSasBuilder(permissions, expires) { BlobContainerName = container.Name, Resource = "c" };
        var sas = builder.ToSasQueryParameters(key.Value, service.AccountName);
        return new UriBuilder(container.Uri) { Query = sas.ToString() }.Uri;
    }

    public async Task DeleteBuildAsync(string deckSlug, string buildId, CancellationToken ct = default)
    {
        var name = ContainerName(deckSlug, buildId);
        var resp = await service.GetBlobContainerClient(name).DeleteIfExistsAsync(cancellationToken: ct);
        if (resp.Value) log.LogInformation("Deleted artifact container {Container}", name);
    }

    private static FileExtensionContentTypeProvider CreateContentTypes()
    {
        var p = new FileExtensionContentTypeProvider();
        p.Mappings[".mjs"] = "text/javascript";
        p.Mappings[".wasm"] = "application/wasm";
        p.Mappings[".webmanifest"] = "application/manifest+json";
        p.Mappings[".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
        p.Mappings[".avif"] = "image/avif";
        return p;
    }
}
