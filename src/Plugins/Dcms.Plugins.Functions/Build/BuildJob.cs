using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.Functions.Build;

/// <summary>A build of the repository's release, where a push or a person asked for one.</summary>
public sealed record BuildPayload(string Sha);

/// <summary>
/// Builds one commit of an instance's repository into a deployment (ADR 0029): the files at that
/// commit, type-checked and bundled by script-runner against this environment's SDK, each
/// function's definition read and validated. A good build is <c>Ready</c>, waiting for someone
/// who holds what it uses to deploy it; a bad one is <c>Failed</c> with every problem's file and
/// line. Either way the job succeeds: the outcome is the deployment's, not the worker's.
/// </summary>
public sealed class BuildJob(FunctionsDbContext db, Builder builder, ILogger<BuildJob> logger) : IPluginJobHandler
{
    public const string Name = "build";
    public const string HttpClientName = "functions-script-runner";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Queues a build of <paramref name="sha"/> for the instance, once per commit.</summary>
    /// <param name="attempt">How many builds of this commit exist: a rebuild is a new job, a redelivered push is not.</param>
    public static Task<JobTicket> EnqueueAsync(IPluginContext context, Guid instanceId, string sha, string? slug, CancellationToken ct, int attempt = 0) =>
        context.Contracts.Get<IPluginTrackedJobs>().EnqueueAsync(new TrackedJobEnqueue(
            Name,
            JsonSerializer.SerializeToElement(new BuildPayload(sha), Json),
            InstanceId: instanceId,
            // Per commit: the same commit pushed twice is one build; a retry is the ledger's.
            DedupeKey: $"functions-build:{instanceId}:{sha}:{attempt}",
            TitleKey: "jobs.kinds.functionsBuild.title",
            TitleParams: new Dictionary<string, string> { ["commit"] = sha.Length > 7 ? sha[..7] : sha },
            RequiredPermission: "read",
            Links: slug is null ? null : [new TrackedJobLink("result", $"/apps/{slug}/deployments")]), ct);

    public async Task RunAsync(JsonElement? payload, IPluginContext context, CancellationToken ct)
    {
        var sha = payload?.Deserialize<BuildPayload>(Json)?.Sha;
        if (string.IsNullOrWhiteSpace(sha) || context.Instance is not { } instance)
        {
            throw new ContractValidationException("A build needs its instance and commit.");
        }
        using var rls = RlsScope.Environment(context.TenantId, context.EnvironmentId);

        if (await db.Deployments.AnyAsync(d => d.InstanceId == instance.InstanceId && d.CommitSha == sha
                                               && d.Status != FunctionDeploymentStatus.Failed, ct))
        {
            return; // Built already: a redelivered push, or a rebuild of what is there.
        }
        var number = (await db.Deployments.Where(d => d.InstanceId == instance.InstanceId).MaxAsync(d => (int?)d.Number, ct) ?? 0) + 1;
        var deployment = new FunctionDeployment
        {
            Id = Guid.CreateVersion7(),
            TenantId = context.TenantId,
            EnvironmentId = context.EnvironmentId,
            InstanceId = instance.InstanceId,
            Number = number,
            CommitSha = sha,
            CreatedBy = context.Actor.Id?.ToString() ?? context.Actor.Key,
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(ct);

        try
        {
            var files = await context.Contracts.Get<IPluginRepos>().ReadFilesAsync(new RepoFilesQuery(sha, instance.InstanceId), ct);
            var built = await builder.BuildAsync(files.Files, ct);
            if (!built.Ok)
            {
                Fail(deployment, built.Diagnostics);
            }
            else
            {
                deployment.Manifest = FunctionManifest.Serialize(built.Functions);
                deployment.Bundles = JsonSerializer.Serialize(built.Bundles);
                deployment.Status = FunctionDeploymentStatus.Ready;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Building deployment {Deployment} failed to reach script-runner.", deployment.Id);
            Fail(deployment, [new Diagnostic("The build service could not be reached. Try again in a moment.")]);
        }
        await db.SaveChangesAsync(ct);
    }

    private static void Fail(FunctionDeployment deployment, IReadOnlyList<Diagnostic> diagnostics)
    {
        deployment.Status = FunctionDeploymentStatus.Failed;
        deployment.Diagnostics = JsonSerializer.Serialize(diagnostics.Take(200), Json);
        var first = diagnostics.FirstOrDefault()?.Message ?? "The build failed.";
        deployment.Error = first.Length > 4000 ? first[..4000] : first;
    }

    /// <summary>A build problem, as script-runner reports one.</summary>
    public sealed record Diagnostic(string Message, string? File = null, int? Line = null, int? Column = null, string? Code = null);

}

/// <summary>A push to an instance's release branch builds it; pushes to any other branch are work in progress.</summary>
public sealed class RepoPushedHandler : IPluginEventHandler<RepoPushed>
{
    public async Task HandleAsync(RepoPushed pushed, IPluginContext context, CancellationToken ct)
    {
        if (pushed.PluginId != FunctionsPlugin.PluginId || pushed.Branch != "release" || string.IsNullOrWhiteSpace(pushed.Sha))
        {
            return;
        }
        await BuildJob.EnqueueAsync(context, pushed.InstanceId, pushed.Sha, slug: null, ct);
    }
}
