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
    public static Dictionary<string, string> For(BuildRequest r) => new(StringComparer.Ordinal)
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
        ["PODIUM_DECK_TITLE"] = r.Deck.Title,
    };
}

/// <summary>Starts an execution of the pre-provisioned Container Apps Job, overriding only env vars and resources.</summary>
public sealed class ContainerAppsJobRunner(ArmClient arm, IOptions<BuilderOptions> options, ILogger<ContainerAppsJobRunner> log) : IBuildRunner
{
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
