using System.Diagnostics;
using System.Globalization;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Microsoft.Extensions.Options;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Web.Configuration;

namespace Podium.Web.Builds;

/// <summary>Environment contract shared by every runner and consumed by builder/build.mjs.</summary>
public static class BuilderEnvironment
{
    public static Dictionary<string, string> For(BuildRequest r)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PODIUM_BUILD_ID"] = r.Build.Id,
            ["PODIUM_DECK_SLUG"] = r.Deck.Slug,
            ["PODIUM_DECK_KIND"] = r.Deck.Kind.ToString().ToLowerInvariant(),
            ["PODIUM_DECK_PATH"] = r.Deck.Path,
            ["PODIUM_DECK_ENTRY"] = r.Deck.Entry,
            ["PODIUM_BASE_PATH"] = $"/d/{r.Deck.Slug}/",
            ["PODIUM_CLONE_URL"] = r.CloneUrl.ToString(),
            ["PODIUM_SHA"] = r.Build.Sha,
            ["PODIUM_UPLOAD_URL"] = r.UploadUri.ToString(),
            ["PODIUM_CALLBACK_URL"] = r.CallbackUri.ToString(),
            ["PODIUM_CALLBACK_TOKEN"] = r.CallbackToken,
            ["PODIUM_TIMEOUT_SEC"] = ((int)r.Timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["PODIUM_EXPORT_PDF"] = r.Deck.ExportPdf ? "1" : "0",
            ["PODIUM_EXPORT_PPTX"] = r.Deck.ExportPptx ? "1" : "0",
            ["PODIUM_TRUSTED"] = r.Source.Trusted ? "1" : "0",
            ["PODIUM_STRIP_NOTES"] = r.Deck.StripNotesForViewers ? "1" : "0",
            ["PODIUM_NPM_SCRIPTS"] = r.Source.Trusted && r.Deck.NpmScripts ? "1" : "0",
            ["PODIUM_DECK_TITLE"] = r.Deck.Title,
        };
        if (r.MaxOutputMegabytes > 0) env["PODIUM_MAX_OUTPUT_MB"] = r.MaxOutputMegabytes.ToString(CultureInfo.InvariantCulture);
        return env;
    }
}

/// <summary>Starts an execution of the pre-provisioned Container Apps Job, overriding only env vars and resources.</summary>
public sealed class ContainerAppsJobRunner(ArmClient arm, IOptions<BuilderOptions> options, ILogger<ContainerAppsJobRunner> log) : IBuildRunner
{
    private (BuilderVersion Value, DateTimeOffset At)? _versionCache;

    /// <summary>
    /// The job template's image plus the per-kind fingerprints the deployment put on it (PODIUM_BUILDER_FINGERPRINTS,
    /// computed by builder/fingerprint.mjs); without the variable the image reference is the version for every kind.
    /// </summary>
    public async Task<BuilderVersion> GetBuilderVersionAsync(CancellationToken ct = default)
    {
        if (_versionCache is { } c && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(2)) return c.Value;
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.JobResourceId)) throw new InvalidOperationException("Builder:JobResourceId is not configured.");
        var job = arm.GetContainerAppJobResource(new ResourceIdentifier(o.JobResourceId));
        var data = await job.GetAsync(ct);
        var container = data.Value.Data.Template.Containers.FirstOrDefault();
        var image = !string.IsNullOrWhiteSpace(o.Image) ? o.Image : container?.Image ?? "unknown";
        var kinds = BuilderVersion.ParseKinds(container?.Env.FirstOrDefault(e => e.Name == FingerprintsVariable)?.Value);
        var version = new BuilderVersion(image, kinds);
        _versionCache = (version, DateTimeOffset.UtcNow);
        return version;
    }

    /// <summary>Set on the builder job by deploy.yml: "slidev=...,presenterm=...,static=...,powerpoint=...,pdf=...".</summary>
    public const string FingerprintsVariable = "PODIUM_BUILDER_FINGERPRINTS";

    public async Task<string> StartAsync(BuildRequest request, CancellationToken ct = default)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.JobResourceId)) throw new InvalidOperationException("Builder:JobResourceId is not configured.");

        var job = arm.GetContainerAppJobResource(new ResourceIdentifier(o.JobResourceId));
        var jobData = await job.GetAsync(ct);
        var templateContainer = jobData.Value.Data.Template.Containers.FirstOrDefault() ?? throw new InvalidOperationException("Job template has no containers.");

        var container = new JobExecutionContainer
        {
            Name = templateContainer.Name,
            Image = o.Image ?? templateContainer.Image,
            Resources = new AppContainerResources { Cpu = o.Cpu, Memory = o.Memory },
        };
        foreach (var (k, v) in BuilderEnvironment.For(request))
            container.Env.Add(new ContainerAppEnvironmentVariable { Name = k, Value = v });

        var template = new ContainerAppJobExecutionTemplate();
        template.Containers.Add(container);

        // The start operation completes once the execution exists; never block on the execution itself.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        var op = await job.StartAsync(WaitUntil.Started, template, cts.Token);
        string name;
        try
        {
            var result = await op.WaitForCompletionAsync(cts.Token);
            name = result.Value.Name;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Execution is running but the LRO did not settle in time; find it by recency.
            var latest = await job.GetContainerAppJobExecutions().GetAllAsync(cancellationToken: ct).FirstOrDefaultAsync(ct);
            name = latest?.Data.Name ?? $"unknown-{request.Build.Id}";
            log.LogWarning("Job start LRO for build {Build} did not complete in 90s; using execution {Execution}", request.Build.Id, name);
        }
        log.LogInformation("Started job execution {Execution} for build {Build}", name, request.Build.Id);
        return name;
    }
}

/// <summary>
/// Development runner: spawns "node build.mjs" locally with the same environment contract. Never use in production;
/// it offers no isolation from the host.
/// </summary>
public sealed class LocalProcessRunner(IOptions<BuilderOptions> options, IHostEnvironment env, ILogger<LocalProcessRunner> log) : IBuildRunner
{
    private (BuilderVersion Value, DateTimeOffset At)? _versionCache;

    /// <summary>
    /// Per-kind fingerprints from the builder's own fingerprint.mjs (the same numbers the deployment publishes on the
    /// job), with a hash of the whole builder folder as the overall identity, so local edits trigger rebuilds of the
    /// kinds they affect just like a deployment would. Cached briefly; a failing node falls back to the overall hash.
    /// </summary>
    public async Task<BuilderVersion> GetBuilderVersionAsync(CancellationToken ct = default)
    {
        if (_versionCache is { } c && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(1)) return c.Value;
        var script = Path.GetFullPath(options.Value.LocalScriptPath ?? throw new InvalidOperationException("Builder:LocalScriptPath is not configured."));
        var builderDir = Path.GetDirectoryName(script)!;
        var files = new List<string> { script };
        foreach (var sub in new[] { "addon", "kinds", "lib" })
        {
            var dir = Path.Combine(builderDir, sub);
            if (Directory.Exists(dir)) files.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal));
        }
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var f in files) sha.AppendData(File.ReadAllBytes(f));
        var overall = "local-" + Convert.ToHexString(sha.GetHashAndReset())[..16].ToLowerInvariant();

        IReadOnlyDictionary<string, string>? kinds = null;
        var fingerprint = Path.Combine(builderDir, "fingerprint.mjs");
        if (File.Exists(fingerprint))
        {
            try
            {
                var psi = new ProcessStartInfo(options.Value.NodeExecutable) { ArgumentList = { fingerprint, "--env" }, WorkingDirectory = builderDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start node.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
                await p.WaitForExitAsync(timeout.Token);
                if (p.ExitCode == 0) kinds = BuilderVersion.ParseKinds(output.Trim());
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Builder fingerprints unavailable; using the overall hash");
            }
        }
        var version = new BuilderVersion(overall, kinds);
        _versionCache = (version, DateTimeOffset.UtcNow);
        return version;
    }

    public Task<string> StartAsync(BuildRequest request, CancellationToken ct = default)
    {
        if (!env.IsDevelopment()) throw new InvalidOperationException("LocalProcess builder is only allowed in the Development environment.");
        var script = options.Value.LocalScriptPath ?? throw new InvalidOperationException("Builder:LocalScriptPath is not configured.");
        var workDir = Path.Combine(Path.GetTempPath(), "podium-builds", request.Build.Id);
        Directory.CreateDirectory(workDir);

        var psi = new ProcessStartInfo(options.Value.NodeExecutable)
        {
            ArgumentList = { Path.GetFullPath(script) },
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (k, v) in BuilderEnvironment.For(request)) psi.Environment[k] = v;
        psi.Environment["PODIUM_WORKDIR"] = workDir;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start node.");
        var id = $"pid-{process.Id}";
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.LogInformation("[builder {Build}] {Line}", request.Build.Id, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.LogWarning("[builder {Build}] {Line}", request.Build.Id, e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = process.WaitForExitAsync(CancellationToken.None).ContinueWith(_ =>
        {
            log.LogInformation("Local builder {Build} exited with {Code}", request.Build.Id, process.ExitCode);
            process.Dispose();
        }, TaskScheduler.Default);
        return Task.FromResult(id);
    }
}
