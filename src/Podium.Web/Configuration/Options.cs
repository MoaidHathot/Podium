using System.ComponentModel.DataAnnotations;

namespace Podium.Web.Configuration;

public sealed class PodiumOptions
{
    public const string Section = "Podium";

    /// <summary>Public origin, e.g. https://slides.moaid.codes</summary>
    [Required] public required Uri PublicBaseUrl { get; set; }

    /// <summary>Optional base URL for builder callbacks (e.g. the platform FQDN); defaults to PublicBaseUrl.</summary>
    public Uri? CallbackBaseUrl { get; set; }

    /// <summary>
    /// Separate origin from which decks of untrusted (external) sources are served, so their code can never touch the
    /// owner session or private decks. Typically the platform FQDN. When unset, external decks cannot be viewed.
    /// </summary>
    public Uri? ExternalBaseUrl { get; set; }

    /// <summary>Numeric GitHub user id of the single owner. Logins can change; ids cannot.</summary>
    [Range(1, long.MaxValue)] public required long OwnerGitHubId { get; set; }

    /// <summary>Base64 key (32+ bytes) used for HMAC tokens. Rotating it invalidates in-flight builder callbacks only.</summary>
    [Required] public required string SigningKey { get; set; }

    /// <summary>How often external (non-installed) sources are polled for changes.</summary>
    public TimeSpan ExternalPollInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Safety-net poll for sources that should be receiving webhooks.</summary>
    public TimeSpan InstalledPollInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Allow adding public repositories that the owner does not control.</summary>
    public bool AllowExternalSources { get; set; } = true;
}

public sealed class GitHubOptions
{
    public const string Section = "GitHub";

    public long? AppId { get; set; }
    public string? AppSlug { get; set; }
    /// <summary>PEM (PKCS#1 or PKCS#8) private key of the GitHub App.</summary>
    public string? PrivateKeyPem { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? WebhookSecret { get; set; }
    /// <summary>Development only: a personal access token used instead of App installation tokens.</summary>
    public string? Token { get; set; }

    public bool AppConfigured => AppId is > 0 && !string.IsNullOrWhiteSpace(PrivateKeyPem);
    public bool OAuthConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Connection string (Azurite / key based). When empty, <see cref="AccountName"/> with managed identity is used.</summary>
    public string? ConnectionString { get; set; }
    public string? AccountName { get; set; }
    public string TablePrefix { get; set; } = "podium";
}

public sealed class BuilderOptions
{
    public const string Section = "Builder";

    /// <summary>ContainerAppsJob or LocalProcess.</summary>
    public string Mode { get; set; } = "ContainerAppsJob";

    /// <summary>ARM resource id of the Container Apps Job used for builds.</summary>
    public string? JobResourceId { get; set; }
    /// <summary>Image to run; when null the job's template image is used.</summary>
    public string? Image { get; set; }
    public double Cpu { get; set; } = 1.0;
    public string Memory { get; set; } = "2Gi";

    /// <summary>LocalProcess mode: path to builder/build.mjs.</summary>
    public string? LocalScriptPath { get; set; }

    /// <summary>Rebuild every deck when the builder image/script changes. On in production; off locally, where every edit to build.mjs would otherwise trigger a storm.</summary>
    public bool AutoRebuildOnUpgrade { get; set; } = true;
    public string NodeExecutable { get; set; } = "node";
}
