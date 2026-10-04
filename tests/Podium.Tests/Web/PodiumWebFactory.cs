using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Web.Builds;

namespace Podium.Tests.Web;

/// <summary>
/// Boots the real pipeline (auth, rate limiting, external-host isolation, serving, API) on in-memory stores with
/// a fake artifact store. No Azure, no GitHub, no builder: everything a request touches is seeded by the test.
/// </summary>
public sealed class PodiumWebFactory : WebApplicationFactory<Program>
{
    public const long OwnerId = 1001;
    public const string PublicOrigin = "http://podium.test";
    public const string ExternalOrigin = "http://external.test";

    public FakeArtifactStore Artifacts { get; } = new();
    public RecordingRunner Runner { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("Storage:ConnectionString", "memory");
        builder.UseSetting("Builder:Mode", "LocalProcess");
        builder.UseSetting("Builder:AutoRebuildOnUpgrade", "false");
        builder.UseSetting("Podium:PublicBaseUrl", PublicOrigin);
        builder.UseSetting("Podium:ExternalBaseUrl", ExternalOrigin);
        builder.UseSetting("Podium:OwnerGitHubId", OwnerId.ToString());
        builder.UseSetting("Podium:SigningKey", Convert.ToBase64String(Encoding.UTF8.GetBytes("integration-test-signing-key-0123456789")));
        builder.UseSetting("Auth:AllowDevLogin", "true");
        builder.UseSetting("GitHub:WebhookSecret", "whsec-test");
        builder.UseSetting("GitHub:Token", "");
        builder.UseSetting("GitHub:AppId", "0");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");

        builder.ConfigureTestServices(services =>
        {
            // Background work (periodic sync, retention sweeps) would reach out to GitHub; tests seed state directly.
            foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MaintenanceService)).ToList())
                services.Remove(d);
            services.AddSingleton<IArtifactStore>(Artifacts);
            // Builds "start" instantly and are recorded; nothing is cloned or executed.
            foreach (var d in services.Where(d => d.ServiceType == typeof(IBuildRunner)).ToList()) services.Remove(d);
            services.AddSingleton<IBuildRunner>(Runner);
        });
    }

    /// <summary>Client that does not follow redirects, pinned to the public origin so Host checks behave like production.</summary>
    public HttpClient Client(string origin = PublicOrigin, string? forwardedFor = null)
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri(origin) });
        // Each test gets its own rate-limit partition; the pipeline trusts X-Forwarded-For (Container Apps ingress).
        c.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor ?? $"10.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}");
        return c;
    }

    public async Task<HttpClient> OwnerClientAsync()
    {
        var c = Client();
        var r = await c.GetAsync("/dev-login?returnUrl=/");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        c.DefaultRequestHeaders.Add("X-Podium-Request", "1");
        return c;
    }

    public async Task<HttpClient> GuestClientAsync(long githubId = 424242)
    {
        var c = Client();
        var r = await c.GetAsync($"/dev-login-guest?returnUrl=/&id={githubId}");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        c.DefaultRequestHeaders.Add("X-Podium-Request", "1");
        return c;
    }

    public static HttpRequestMessage Navigation(string url) => new(HttpMethod.Get, url) { Headers = { Accept = { new MediaTypeWithQualityHeaderValue("text/html") } } };

    /// <summary>Seeds a source + deck with a served build whose site has a single index.html.</summary>
    public async Task<Deck> SeedDeckAsync(string slug, Visibility visibility = Visibility.Private, bool trusted = true, string? alias = null, DeckKind kind = DeckKind.Slidev, string? html = null, bool archived = false)
    {
        var sources = Services.GetRequiredService<ISourceStore>();
        var decks = Services.GetRequiredService<IDeckStore>();
        var builds = Services.GetRequiredService<IBuildStore>();
        var sourceId = trusted ? "owner/slides" : "stranger/public-deck";
        if (await sources.GetAsync(sourceId) is null)
            await sources.UpsertAsync(new Source { Id = sourceId, Owner = sourceId.Split('/')[0], Repo = sourceId.Split('/')[1], Trusted = trusted });
        var buildId = $"b{Interlocked.Increment(ref _buildSeq):d4}";
        var deck = new Deck
        {
            Slug = slug, Alias = alias, SourceId = sourceId, Path = slug, Entry = "slides.md", Kind = kind, Title = $"Deck {slug}",
            Description = $"About {slug}", Visibility = visibility, CurrentBuildId = buildId, LatestSuccessfulBuildId = buildId, LatestBuildId = buildId,
            LatestBuildStatus = BuildStatus.Succeeded, CurrentHasThumbnail = true, Archived = archived,
        };
        await decks.UpsertAsync(deck);
        await builds.UpsertAsync(new Build { Id = buildId, DeckSlug = slug, Sha = "0123456789abcdef0123456789abcdef01234567", Status = BuildStatus.Succeeded, HasSite = true, HasThumbnail = true, FinishedAt = DateTimeOffset.UtcNow });
        Artifacts.PutSiteFile(slug, buildId, "index.html", html ?? $"<!doctype html><html><head><title>{slug}</title></head><body><h1>{slug}</h1></body></html>");
        Artifacts.PutArtifact(slug, buildId, ArtifactKind.Thumbnail, "image/jpeg", [0xFF, 0xD8, 0xFF, 0xD9]);
        Services.GetRequiredService<Podium.Web.Serving.DeckAccessService>().Invalidate(slug);
        return deck;
    }

    private static int _buildSeq;
}

/// <summary>Records every build request the service hands to the runner.</summary>
public sealed class RecordingRunner : IBuildRunner
{
    private readonly List<BuildRequest> _started = [];
    public IReadOnlyList<BuildRequest> Started { get { lock (_started) return _started.ToList(); } }
    public Task<string> StartAsync(BuildRequest request, CancellationToken ct = default)
    {
        lock (_started) _started.Add(request);
        return Task.FromResult("exec-" + request.Build.Id);
    }
    public Task<string> GetBuilderVersionAsync(CancellationToken ct = default) => Task.FromResult("test-builder");
}

/// <summary>Artifact store backed by dictionaries; mirrors what the builder uploads per build.</summary>
public sealed class FakeArtifactStore : IArtifactStore
{
    private readonly Dictionary<string, (string ContentType, byte[] Bytes)> _files = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    public List<(string Slug, string BuildId)> Deleted { get; } = [];

    public void PutSiteFile(string slug, string buildId, string relativePath, string content, string variant = "site")
    {
        lock (_gate) _files[$"{slug}/{buildId}/{variant}/{relativePath}"] = ("text/html; charset=utf-8", Encoding.UTF8.GetBytes(content));
    }

    public void PutArtifact(string slug, string buildId, ArtifactKind kind, string contentType, byte[] bytes)
    {
        lock (_gate) _files[$"{slug}/{buildId}/{kind}"] = (contentType, bytes);
    }

    public Task<ArtifactObject?> OpenSiteFileAsync(string deckSlug, string buildId, string relativePath, CancellationToken ct = default, string variant = "site")
        => Task.FromResult(Open($"{deckSlug}/{buildId}/{variant}/{relativePath}"));

    public Task<ArtifactObject?> OpenArtifactAsync(string deckSlug, string buildId, ArtifactKind kind, CancellationToken ct = default)
        => Task.FromResult(Open($"{deckSlug}/{buildId}/{kind}"));

    public Task<Uri> CreateUploadUriAsync(string deckSlug, string buildId, TimeSpan lifetime, CancellationToken ct = default)
        => Task.FromResult(new Uri($"https://fake.invalid/{deckSlug}/{buildId}?sig=test"));

    public Task DeleteBuildAsync(string deckSlug, string buildId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Deleted.Add((deckSlug, buildId));
            foreach (var k in _files.Keys.Where(k => k.StartsWith($"{deckSlug}/{buildId}/", StringComparison.Ordinal)).ToList()) _files.Remove(k);
        }
        return Task.CompletedTask;
    }

    private ArtifactObject? Open(string key)
    {
        lock (_gate)
        {
            return _files.TryGetValue(key, out var f)
                ? new ArtifactObject(new MemoryStream(f.Bytes, writable: false), f.ContentType, f.Bytes.Length, null, null)
                : null;
        }
    }
}
