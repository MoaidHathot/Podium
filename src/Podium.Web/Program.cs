using Azure.Monitor.OpenTelemetry.AspNetCore;
using System.Security.Claims;
using AspNet.Security.OAuth.GitHub;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.InMemory;
using Podium.Core.Models;
using Podium.Core.Services;
using Podium.Web.Api;
using Podium.Web.Builds;
using Podium.Web.Configuration;
using Podium.Web.GitHub;
using Podium.Web.Security;
using Podium.Web.Serving;
using Podium.Web.Storage;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ----- Options -----
builder.Services.AddOptions<PodiumOptions>().Bind(config.GetSection(PodiumOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<GitHubOptions>().Bind(config.GetSection(GitHubOptions.Section));
builder.Services.AddOptions<StorageOptions>().Bind(config.GetSection(StorageOptions.Section));
builder.Services.AddOptions<BuilderOptions>().Bind(config.GetSection(BuilderOptions.Section));
builder.Services.AddOptions<BuildOptions>().Bind(config.GetSection("Builder")).Configure<IOptions<PodiumOptions>>((o, p) => { o.PublicBaseUrl = p.Value.PublicBaseUrl; o.CallbackBaseUrl = p.Value.CallbackBaseUrl; });

// ----- Storage -----
var storage = config.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
if (string.Equals(storage.ConnectionString, "memory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ISourceStore, InMemorySourceStore>();
    builder.Services.AddSingleton<IDeckStore, InMemoryDeckStore>();
    builder.Services.AddSingleton<IBuildStore, InMemoryBuildStore>();
    builder.Services.AddSingleton<IGrantStore, InMemoryGrantStore>();
    builder.Services.AddSingleton<IShareLinkStore, InMemoryShareLinkStore>();
    builder.Services.AddSingleton<IViewHistoryStore, InMemoryViewHistoryStore>();
    builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
    builder.Services.AddSingleton<IAccessRequestStore, InMemoryAccessRequestStore>();
    builder.Services.AddSingleton<IDeviceStore, InMemoryDeviceStore>();
    builder.Services.AddSingleton<ITalkStore, InMemoryTalkStore>();
    builder.Services.AddSingleton<IAuditStore, InMemoryAuditStore>();
    builder.Services.AddSingleton<ISettingsStore, InMemorySettingsStore>();
    builder.Services.AddSingleton<IArtifactStore, LocalArtifactStore>();
}
else
{
    var credential = new DefaultAzureCredential();
    if (!string.IsNullOrWhiteSpace(storage.ConnectionString))
    {
        builder.Services.AddSingleton(new TableServiceClient(storage.ConnectionString));
        builder.Services.AddSingleton(new BlobServiceClient(storage.ConnectionString));
    }
    else
    {
        var account = storage.AccountName ?? throw new InvalidOperationException("Storage:AccountName or Storage:ConnectionString is required.");
        builder.Services.AddSingleton(new TableServiceClient(new Uri($"https://{account}.table.core.windows.net"), credential));
        builder.Services.AddSingleton(new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), credential));
    }
    builder.Services.AddSingleton(sp => new TableClients(sp.GetRequiredService<TableServiceClient>(), storage.TablePrefix));
    builder.Services.AddSingleton<ISourceStore, TableSourceStore>();
    builder.Services.AddSingleton<IDeckStore, TableDeckStore>();
    builder.Services.AddSingleton<IBuildStore, TableBuildStore>();
    builder.Services.AddSingleton<IGrantStore, TableGrantStore>();
    builder.Services.AddSingleton<IShareLinkStore, TableShareLinkStore>();
    builder.Services.AddSingleton<IViewHistoryStore, TableViewHistoryStore>();
    builder.Services.AddSingleton<ISessionStore, TableSessionStore>();
    builder.Services.AddSingleton<IAccessRequestStore, TableAccessRequestStore>();
    builder.Services.AddSingleton<IDeviceStore, TableDeviceStore>();
    builder.Services.AddSingleton<ITalkStore, TableTalkStore>();
    builder.Services.AddSingleton<IAuditStore, TableAuditStore>();
    builder.Services.AddSingleton<ISettingsStore, TableSettingsStore>();
    builder.Services.AddSingleton<IArtifactStore, BlobArtifactStore>();

    // Data protection keys (cookie encryption) must survive restarts and scale-out. In production the key ring is
    // additionally wrapped with a Key Vault RSA key, so the XML in Blob is useless without Key Vault access.
    var dp = builder.Services.AddDataProtection()
        .SetApplicationName("Podium")
        .PersistKeysToAzureBlobStorage(sp =>
        {
            var container = sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("podium-system");
            container.CreateIfNotExists();
            return container.GetBlobClient("dataprotection-keys.xml");
        });
    var dpKeyId = config["DataProtection:KeyVaultKeyId"];
    if (!string.IsNullOrWhiteSpace(dpKeyId))
        dp.ProtectKeysWithAzureKeyVault(new Uri(dpKeyId), credential);
    else if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("DataProtection:KeyVaultKeyId must be set outside Development so cookie keys are encrypted at rest.");
}

// ----- Telemetry: requests, dependencies, exceptions and traces to Application Insights (OpenTelemetry) -----
// Only when a connection string is configured (production via Bicep); local runs keep console logs only.
if (!string.IsNullOrWhiteSpace(config["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor(o =>
    {
        o.SamplingRatio = 1.0f; // tiny traffic; keep everything
        // The connection string carries only an instrumentation key (not a secret); ingestion is keyed, not Entra-authenticated.
    });
}

// ----- Builder runner -----
var builderMode = config.GetSection(BuilderOptions.Section).Get<BuilderOptions>()?.Mode ?? "ContainerAppsJob";
if (string.Equals(builderMode, "LocalProcess", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IBuildRunner, LocalProcessRunner>();
}
else
{
    builder.Services.AddSingleton(new ArmClient(new DefaultAzureCredential()));
    builder.Services.AddSingleton<IBuildRunner, ContainerAppsJobRunner>();
}

// ----- Domain services -----
builder.Services.AddMemoryCache();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSingleton<HmacTokenService>();
builder.Services.AddSingleton<IBuildTokenService>(sp => sp.GetRequiredService<HmacTokenService>());
builder.Services.AddSingleton<GitHubAppAuth>();
builder.Services.AddSingleton<IRepositoryClient, GitHubRepositoryClient>();
builder.Services.AddSingleton<Podium.Core.Abstractions.IBuildObserver, GitHubChecksObserver>();
builder.Services.AddScoped<BuildService>();
builder.Services.AddOptions<LiveSessionOptions>().Bind(config.GetSection("Sessions"));
builder.Services.AddSingleton<SessionRecorders>();
builder.Services.AddSingleton<DeckSearchIndex>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<DeckSyncService>();
builder.Services.AddSingleton<SyncQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncQueue>());
builder.Services.AddSingleton<InstallationDiscovery>();
builder.Services.AddSingleton<GitHubWebhookHandler>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddSingleton<CallerResolver>();
builder.Services.AddSingleton<SecurityStamp>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<ViewTokenService>();
builder.Services.AddSingleton<DeckAccessService>();
builder.Services.AddOptions<Podium.Web.Sync.AudienceOptions>().Bind(config.GetSection(Podium.Web.Sync.AudienceOptions.Section));
builder.Services.AddSingleton<Podium.Web.Sync.AudienceService>();
builder.Services.AddSingleton<Podium.Web.Security.DeviceService>();
builder.Services.AddSingleton<Podium.Web.Sync.SyncHub>();

// ----- Auth -----
var gh = config.GetSection(GitHubOptions.Section).Get<GitHubOptions>() ?? new GitHubOptions();
var auth = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "podium_session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/denied";
        o.ReturnUrlParameter = "returnUrl";
        // With the public gallery on, "/" is a portfolio for everyone who is not the owner (anonymous or signed in).
        var galleryEnabled = config.GetValue<bool>("Podium:PublicGallery");
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; }
            if (galleryEnabled && ctx.Request.Path == "/") { ctx.Response.Redirect("/gallery" + ctx.Request.QueryString); return Task.CompletedTask; }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; }
            if (galleryEnabled && ctx.Request.Path == "/") { ctx.Response.Redirect("/gallery" + ctx.Request.QueryString); return Task.CompletedTask; }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        // "Sign out everywhere": tickets issued before the security stamp are rejected and the cookie cleared.
        // Per-device sign-out: every login records a device with a random sid in the ticket; a revoked device's
        // tickets are rejected on their next request (30 s cache). Tickets without a sid predate device tracking.
        o.Events.OnSigningIn = async ctx =>
        {
            var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
            if (identity.HasClaim(c => c.Type == Podium.Web.Security.DeviceService.SidClaim)) return;
            var caller = ctx.HttpContext.RequestServices.GetRequiredService<CallerResolver>().Resolve(ctx.Principal);
            if (caller.Principal is null) return;
            try
            {
                var sid = await ctx.HttpContext.RequestServices.GetRequiredService<Podium.Web.Security.DeviceService>().RegisterAsync(caller.Principal, ctx.HttpContext, ctx.HttpContext.RequestAborted);
                identity.AddClaim(new Claim(Podium.Web.Security.DeviceService.SidClaim, sid));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Device tracking must never stand between the owner and their sign-in; the ticket is simply untracked.
                ctx.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>().LogWarning(ex, "Device could not be recorded at sign-in");
            }
        };
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var stamp = ctx.HttpContext.RequestServices.GetRequiredService<SecurityStamp>();
            if (!await stamp.IsTicketValidAsync(ctx.Properties.IssuedUtc, ctx.HttpContext.RequestAborted))
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }
            var sid = Podium.Web.Security.DeviceService.SidOf(ctx.Principal);
            if (sid is null) return;
            var caller = ctx.HttpContext.RequestServices.GetRequiredService<CallerResolver>().Resolve(ctx.Principal);
            if (caller.Principal is null) return;
            var devices = ctx.HttpContext.RequestServices.GetRequiredService<Podium.Web.Security.DeviceService>();
            bool revoked;
            try { revoked = await devices.IsRevokedAsync(caller.Principal, sid, ctx.HttpContext.RequestAborted); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Storage trouble: the stamp check above already passed; do not add a lock-out on top of an outage.
                ctx.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>().LogWarning(ex, "Device revocation check failed; allowing the ticket");
                return;
            }
            if (revoked)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }
            await devices.TouchAsync(caller.Principal, sid, ctx.HttpContext, ctx.HttpContext.RequestAborted);
        };
    });

if (gh.OAuthConfigured)
{
    auth.AddGitHub(o =>
    {
        o.ClientId = gh.ClientId!;
        o.ClientSecret = gh.ClientSecret!;
        o.CallbackPath = "/signin-github";
        o.SaveTokens = false;
        o.Scope.Clear(); // GitHub App user tokens carry the App's permissions; we only need the public profile.
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.ClaimActions.MapJsonKey(PodiumClaims.GitHubId, "id");
        o.ClaimActions.MapJsonKey(PodiumClaims.Login, "login");
        o.ClaimActions.MapJsonKey(PodiumClaims.Avatar, "avatar_url");
        o.Events.OnCreatingTicket = ctx =>
        {
            // Keep the cookie minimal: id, login, avatar. Nothing else from GitHub is persisted.
            var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
            foreach (var c in identity.Claims.Where(c => c.Type is not (PodiumClaims.GitHubId or PodiumClaims.Login or PodiumClaims.Avatar or ClaimTypes.NameIdentifier or ClaimTypes.Name)).ToList())
                identity.RemoveClaim(c);
            return Task.CompletedTask;
        };
    });
}

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(PodiumClaims.OwnerPolicy, p => p.RequireAssertion(ctx =>
    {
        // View-token identities (external deck origin) are never the owner for API/UI purposes.
        if (ctx.User.HasClaim(c => c.Type == PodiumClaims.ViewToken)) return false;
        var id = ctx.User.FindFirstValue(PodiumClaims.GitHubId) ?? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var owner = ctx.Resource is HttpContext http ? http.RequestServices.GetRequiredService<IOptions<PodiumOptions>>().Value.OwnerGitHubId : 0;
        return long.TryParse(id, out var v) && v == owner && owner > 0;
    }));

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/", PodiumClaims.OwnerPolicy);
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Denied");
    o.Conventions.AllowAnonymousToPage("/Shared");
    o.Conventions.AllowAnonymousToPage("/Gallery");
    o.Conventions.AllowAnonymousToPage("/Error");
    // The talks catalog doubles as the public speaker page; the page models filter to public talks for non-owners.
    o.Conventions.AllowAnonymousToFolder("/Talks");
});
builder.Services.AddHttpContextAccessor();

// ----- Rate limiting (per client IP) -----
// Keeps credential stuffing, share-link guessing and webhook floods cheap to absorb. Deck assets are exempt: a single
// slide deck legitimately fetches dozens of files in a burst.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) => { ctx.HttpContext.Response.Headers.RetryAfter = "10"; return ValueTask.CompletedTask; };
    static string ClientKey(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    static RateLimitPartition<string> Sliding(HttpContext http, int permits) => RateLimitPartition.GetSlidingWindowLimiter(ClientKey(http),
        _ => new SlidingWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 });
    o.AddPolicy("auth", http => Sliding(http, 20));
    o.AddPolicy("webhook", http => Sliding(http, 60));
    o.AddPolicy("probe", http => Sliding(http, 120));
    // Deck entry pages and artifact downloads (not assets): bounds share-link enumeration; far above human navigation.
    // A conference room shares one NAT address; these must comfortably absorb a few hundred people opening a deck at once.
    o.AddPolicy("deck-entry", http => Sliding(http, 300));
    o.AddPolicy("join", http => Sliding(http, 300));
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// `Podium.Web export`: one-shot backup of the tables into the "backups" blob container, then exit (scheduled job).
if (args.Length > 0 && string.Equals(args[0], "export", StringComparison.OrdinalIgnoreCase))
{
    var tables = app.Services.GetService<TableServiceClient>();
    var blobs = app.Services.GetService<BlobServiceClient>();
    if (tables is null || blobs is null) { Console.Error.WriteLine("export needs Azure storage (Storage:AccountName or a connection string)"); return 2; }
    return await BackupCommand.RunAsync(tables, blobs, storage.TablePrefix, Console.Out, CancellationToken.None);
}

// Live sessions record pacing from the sync relay (no extra traffic from clients); session changes reach every window.
{
    var hub = app.Services.GetRequiredService<Podium.Web.Sync.SyncHub>();
    var recorders = app.Services.GetRequiredService<SessionRecorders>();
    hub.OnPresenterPosition = recorders.RecordPositionAsync;
    hub.OnPresence = recorders.RecordPresenceAsync;
    recorders.OnSessionChanged = session => hub.NotifySessionAsync(session);
    app.Services.GetRequiredService<Podium.Web.Sync.AudienceService>().Send = (slug, target, payload) => hub.SendToAsync(slug, target, payload);
    var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
    hub.ResolveLiveSession = async slug =>
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SessionService>().GetLiveAsync(slug);
    };
}
app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation("Podium is shutting down (graceful stop requested)"));

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    // Baseline hardening for every response. Deck pages add their own CSP when needed.
    ctx.Response.Headers["X-Frame-Options"] = ctx.Request.Path.StartsWithSegments("/d") ? "SAMEORIGIN" : "DENY";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    if (Podium.Web.Security.Csp.AppliesTo(ctx.Request.Path))
        ctx.Response.Headers.ContentSecurityPolicy = Podium.Web.Security.Csp.HeaderValue(Podium.Web.Security.Csp.Nonce(ctx));
    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = ctx.File.Name is "sw.js" or "manifest.webmanifest" ? "no-cache" : "public, max-age=3600",
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<ExternalHostMiddleware>();
app.UseAuthorization();

// ----- Login / logout -----
app.MapGet("/login/github", (string? returnUrl) =>
{
    if (!gh.OAuthConfigured) return Results.Problem("GitHub OAuth is not configured.", statusCode: 503);
    var target = SafeReturnUrl(returnUrl);
    return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [GitHubAuthenticationDefaults.AuthenticationScheme]);
}).RequireRateLimiting("auth");
app.MapPost("/logout", async (HttpContext http, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});
// Owner only (policy on the group): invalidates every session cookie, including this one.
app.MapPost("/api/security/sign-out-everywhere", async (HttpContext http, SecurityStamp stamp, AuditService audit, CancellationToken ct) =>
{
    await audit.RecordAsync(http, "security.sign-out-everywhere", null);
    await stamp.BumpAsync(ct);
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { ok = true });
}).RequireAuthorization(PodiumClaims.OwnerPolicy).AddEndpointFilter<Podium.Web.Api.RequestHeaderFilter>();

// Signed-in devices of the owner: list, and sign out one of them (revoking the current one signs this browser out).
app.MapGet("/api/security/devices", async (HttpContext http, CallerResolver callers, Podium.Web.Security.DeviceService devices, CancellationToken ct) =>
{
    var caller = callers.Resolve(http.User);
    var current = Podium.Web.Security.DeviceService.SidOf(http.User);
    var list = await devices.ListAsync(caller.Principal!, ct);
    return Results.Ok(list.Where(d => !d.Revoked || (d.RevokedAt is { } r && r > DateTimeOffset.UtcNow.AddDays(-7))).Select(d => new { d.Sid, d.Client, d.Ip, d.CreatedAt, d.LastSeenAt, d.Revoked, d.RevokedAt, current = d.Sid == current }));
}).RequireAuthorization(PodiumClaims.OwnerPolicy);
app.MapPost("/api/security/devices/{sid}/revoke", async (string sid, HttpContext http, CallerResolver callers, Podium.Web.Security.DeviceService devices, AuditService audit, CancellationToken ct) =>
{
    if (sid.Length > 64 || !sid.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) return Results.BadRequest();
    var caller = callers.Resolve(http.User);
    if (!await devices.RevokeAsync(caller.Principal!, sid, ct)) return Results.NotFound();
    await audit.RecordAsync(http, "security.device-revoke", sid);
    var self = Podium.Web.Security.DeviceService.SidOf(http.User) == sid;
    if (self) await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { ok = true, signedOut = self });
}).RequireAuthorization(PodiumClaims.OwnerPolicy).AddEndpointFilter<Podium.Web.Api.RequestHeaderFilter>();

if (app.Environment.IsDevelopment() && config.GetValue<bool>("Auth:AllowDevLogin"))
{
    // Development convenience: sign in as the owner without GitHub. Never enabled outside Development.
    app.MapGet("/dev-login", async (HttpContext http, IOptions<PodiumOptions> opts, string? returnUrl) =>
    {
        var id = opts.Value.OwnerGitHubId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Same claim set as the real GitHub login, avatar included, so the layout and CSP are exercised identically.
        var identity = new ClaimsIdentity([new Claim(PodiumClaims.GitHubId, id), new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, "dev-owner"), new Claim(PodiumClaims.Login, "dev-owner"), new Claim(PodiumClaims.Avatar, $"https://avatars.githubusercontent.com/u/{id}?v=4")], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Redirect(SafeReturnUrl(returnUrl));
    });
    app.MapGet("/dev-login-guest", async (HttpContext http, string? returnUrl, long id = 424242) =>
    {
        var identity = new ClaimsIdentity([new Claim(PodiumClaims.GitHubId, id.ToString(System.Globalization.CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Name, "dev-guest"), new Claim(PodiumClaims.Login, "dev-guest")], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Redirect(SafeReturnUrl(returnUrl));
    });

    // Development/CI only: seeds a fixture deck (memory storage) whose tiny site speaks the Podium sync protocol, so
    // browser smoke tests can exercise library, details, remote and the relay without a real build.
    app.MapPost("/dev-seed", async (ISourceStore sources, IDeckStore decks, IBuildStore builds, IArtifactStore artifacts, DeckAccessService access, DeckSearchIndex search, ITalkStore talks, CancellationToken ct) =>
    {
        if (artifacts is not LocalArtifactStore local) return Results.BadRequest("dev-seed needs memory storage");
        const string slug = "fixture-deck";
        const string talkId = "fixture-slides-fixture";
        await sources.UpsertAsync(new Source { Id = "fixture/slides", Owner = "fixture", Repo = "slides", Trusted = true, LastSeenSha = new string('a', 40) }, ct);
        async Task SeedSiteAsync(string deckSlug, string textJson)
        {
            var dir = (await local.CreateUploadUriAsync(deckSlug, Podium.Web.Storage.FixtureDeck.BuildId, TimeSpan.FromMinutes(5), ct)).LocalPath;
            Directory.CreateDirectory(Path.Combine(dir, "site", "pages"));
            await File.WriteAllTextAsync(Path.Combine(dir, "site", "index.html"), Podium.Web.Storage.FixtureDeck.IndexHtml(deckSlug), ct);
            for (var p = 1; p <= Podium.Web.Storage.FixtureDeck.Pages; p++)
                await File.WriteAllBytesAsync(Path.Combine(dir, "site", "pages", $"{p:000}.jpg"), Podium.Web.Storage.FixtureDeck.PageJpeg, ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "site", "pages", "index.json"), Podium.Web.Storage.FixtureDeck.PagesJson, ct);
            // Slide sheet (one tile per page) so the remote's thumbnails and go-to grid have something to show.
            await File.WriteAllBytesAsync(Path.Combine(dir, "slides.jpg"), Podium.Web.Storage.FixtureDeck.PageJpeg, ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "slides.json"), $$"""{"count":{{Podium.Web.Storage.FixtureDeck.Pages}},"cols":{{Podium.Web.Storage.FixtureDeck.Pages}},"rows":1,"cellWidth":1,"cellHeight":1}""", ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "notes.json"), "[{\"index\":1,\"title\":\"One\",\"note\":\"First note\"},{\"index\":2,\"title\":\"Two\",\"note\":\"Second note\"},{\"index\":3,\"title\":\"Three\",\"note\":null}]", ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "text.json"), textJson, ct);
        }
        await SeedSiteAsync(slug, "[{\"index\":1,\"title\":\"One\",\"text\":\"fixture slide one\"},{\"index\":2,\"title\":\"Two\",\"text\":\"fixture slide two about the sync relay\"},{\"index\":3,\"title\":\"Three\",\"text\":\"fixture slide three\"}]");
        // A second deck in the same folder: the talk's workshop variant (slide two reworded, so the compare view has a difference to show).
        await SeedSiteAsync($"{slug}-workshop", "[{\"index\":1,\"title\":\"One\",\"text\":\"fixture slide one\"},{\"index\":2,\"title\":\"Two\",\"text\":\"fixture slide two about the sync hub\"},{\"index\":3,\"title\":\"Three\",\"text\":\"fixture slide three\"}]");
        foreach (var (deckSlug, variant, entry) in new[] { (slug, (string?)null, "slides.pdf"), ($"{slug}-workshop", "workshop", "slides.workshop.pdf") })
        {
            await decks.UpsertAsync(new Deck
            {
                Slug = deckSlug, SourceId = "fixture/slides", Path = "fixture", Entry = entry, Kind = DeckKind.Pdf, Title = variant is null ? "Fixture deck" : "Fixture deck (workshop)", Tags = ["fixture"], Variant = variant, TalkId = talkId,
                Visibility = Visibility.Public, CurrentBuildId = Podium.Web.Storage.FixtureDeck.BuildId, LatestSuccessfulBuildId = Podium.Web.Storage.FixtureDeck.BuildId, LatestBuildId = Podium.Web.Storage.FixtureDeck.BuildId, LatestBuildStatus = BuildStatus.Succeeded,
                CurrentHasNotes = true, CurrentHasText = true, CurrentHasSlideSheet = true, CurrentSlideCount = Podium.Web.Storage.FixtureDeck.Pages, LastCommitSha = new string('a', 40),
            }, ct);
            await builds.UpsertAsync(new Build { Id = Podium.Web.Storage.FixtureDeck.BuildId, DeckSlug = deckSlug, Sha = new string('a', 40), Status = BuildStatus.Succeeded, HasSite = true, HasNotes = true, HasText = true, HasSlideSheet = true, SlideCount = Podium.Web.Storage.FixtureDeck.Pages, FinishedAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-20) }, ct);
            access.Invalidate(deckSlug);
            await search.RefreshAsync((await decks.GetAsync(deckSlug, ct))!, ct);
        }
        // The talk both decks belong to, with a history, and the speaker file.
        await talks.UpsertAsync(new Talk
        {
            Id = talkId, LocalId = "fixture", SourceId = "fixture/slides", Path = "fixture", Title = "The fixture talk", Level = "intermediate", Durations = [45, 60], Status = "available", Public = true, Tags = ["fixture", "testing"],
            Abstract = "Three slides that exercise **every** feature of Podium: the pages viewer, the sync relay and the phone remote.\n\nThe second paragraph is only in the long abstract.",
            Parts = [new TalkPart("Short abstract", "Three slides that exercise every feature of Podium."), new TalkPart("Outline", "- Pages\n- Sync\n- Remote"), new TalkPart("Takeaways", "- Smoke tests are decks too")],
            DeckSlugs = [slug, $"{slug}-workshop"], LastCommitAt = DateTimeOffset.UtcNow.AddDays(-3),
            Submissions =
            [
                new Submission { Key = "2025-05-20-smokeconf", Event = "SmokeConf", Date = new DateOnly(2025, 5, 20), Status = "delivered", Format = "talk", Duration = 45, Deck = "slides.pdf", DeckSlug = slug, Location = "Tel Aviv", Url = "https://example.test/smokeconf" },
                new Submission { Key = "workshop-days", Event = "Workshop Days", Date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30), Status = "accepted", Format = "workshop", Duration = 120, Deck = "slides.workshop.pdf", DeckSlug = $"{slug}-workshop" },
                new Submission { Key = "2026-01-15-declined-con", Event = "Declined Con", Date = new DateOnly(2026, 1, 15), Status = "declined", Notes = "They wanted a lightning talk." },
            ],
        }, ct);
        await talks.UpsertSpeakerAsync(new Speaker { SourceId = "fixture/slides", Name = "Fixture Speaker", Tagline = "Tests things for a living", Bio = "Fixture Speaker writes **smoke tests** and talks about them.", Parts = [new TalkPart("Short bio", "Fixture Speaker tests things.")], Links = [new("github", "https://github.com/fixture")] }, ct);
        return Results.Ok(new { slug, talk = talkId });
    });
}

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));
// Deploy guard: counts only. Deployments wait while a session that opted into holding them is live.
app.MapGet("/healthz/live", async (SessionService sessions, Podium.Web.Sync.SyncHub hub, CancellationToken ct) =>
{
    var holding = await sessions.HoldingDeploysAsync(ct);
    return Results.Ok(new { holdDeploys = holding.Count, presenting = hub.RoomsWithPresenters().Count });
}).RequireRateLimiting("probe");
app.MapDeckServing();
Podium.Web.Serving.PresenterToolsEndpoints.MapPresenterTools(app);
app.MapPodiumApi();
Podium.Web.Serving.TalkEndpoints.MapTalks(app);
Podium.Web.Sync.SyncEndpoints.MapSync(app);
app.MapRazorPages();

app.Run();
return 0;

static string SafeReturnUrl(string? returnUrl)
    => !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.StartsWith("/\\", StringComparison.Ordinal) ? returnUrl : "/";

/// <summary>Exposes the entry point to integration tests (WebApplicationFactory).</summary>
public partial class Program { }
