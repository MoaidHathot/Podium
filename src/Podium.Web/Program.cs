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
builder.Services.AddOptions<BuildOptions>().Configure<IOptions<PodiumOptions>>((o, p) => { o.PublicBaseUrl = p.Value.PublicBaseUrl; o.CallbackBaseUrl = p.Value.CallbackBaseUrl; });

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
builder.Services.AddScoped<BuildService>();
builder.Services.AddScoped<DeckSyncService>();
builder.Services.AddSingleton<SyncQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncQueue>());
builder.Services.AddSingleton<InstallationDiscovery>();
builder.Services.AddSingleton<GitHubWebhookHandler>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddSingleton<CallerResolver>();
builder.Services.AddSingleton<ViewTokenService>();
builder.Services.AddSingleton<DeckAccessService>();
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
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
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
    o.Conventions.AllowAnonymousToPage("/Error");
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
    o.AddPolicy("deck-entry", http => Sliding(http, 90));
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();
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
    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=3600",
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

if (app.Environment.IsDevelopment() && config.GetValue<bool>("Auth:AllowDevLogin"))
{
    // Development convenience: sign in as the owner without GitHub. Never enabled outside Development.
    app.MapGet("/dev-login", async (HttpContext http, IOptions<PodiumOptions> opts, string? returnUrl) =>
    {
        var id = opts.Value.OwnerGitHubId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var identity = new ClaimsIdentity([new Claim(PodiumClaims.GitHubId, id), new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, "dev-owner"), new Claim(PodiumClaims.Login, "dev-owner")], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Redirect(SafeReturnUrl(returnUrl));
    });
    app.MapGet("/dev-login-guest", async (HttpContext http, string? returnUrl, long id = 424242) =>
    {
        var identity = new ClaimsIdentity([new Claim(PodiumClaims.GitHubId, id.ToString(System.Globalization.CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Name, "dev-guest"), new Claim(PodiumClaims.Login, "dev-guest")], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Redirect(SafeReturnUrl(returnUrl));
    });
}

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));
app.MapDeckServing();
app.MapPodiumApi();
Podium.Web.Sync.SyncEndpoints.MapSync(app);
app.MapRazorPages();

app.Run();

static string SafeReturnUrl(string? returnUrl)
    => !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.StartsWith("/\\", StringComparison.Ordinal) ? returnUrl : "/";
