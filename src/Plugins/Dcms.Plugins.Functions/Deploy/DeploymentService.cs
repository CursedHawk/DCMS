using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.Functions.Build;
using Dcms.Plugins.Functions.Runtime;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.Functions.Deploy;

public sealed record DeploymentSummary(
    Guid Id, int Number, string CommitSha, string Status, string? Error,
    IReadOnlyList<string> Functions, DateTimeOffset CreatedAt, DateTimeOffset? ActivatedAt, string? ActivatedBy);

public sealed record DeploymentDetail(
    DeploymentSummary Deployment, IReadOnlyList<FunctionSpec> Functions, JsonElement? Diagnostics,
    IReadOnlyList<string> Permissions);

/// <summary>Who could not deploy, and what they lack.</summary>
public sealed class DeployRefusedException(IReadOnlyList<string> missing)
    : Exception("Deploying needs every permission these functions use: " + string.Join(", ", missing) + ".")
{
    public IReadOnlyList<string> Missing { get; } = missing;
}

/// <summary>
/// An instance's repository and deployments (ADR 0029). Making a deployment live is definer's
/// rights: whoever deploys must hold every permission its functions use, and that set is frozen
/// on it, so a call at run time proceeds only while it is there. A rollback makes an older
/// deployment live again under the set it was vouched for, which the person rolling back must
/// hold too: nobody revives what they could not have deployed.
/// </summary>
public sealed class DeploymentService(
    FunctionsDbContext db, IPluginContext context, IHttpContextAccessor http, IAuthorizationService authz,
    PluginRegistry registry, FunctionRunner runner)
{
    /// <summary>What a new repository starts with: one function to call, and how to write more.</summary>
    public static readonly IReadOnlyDictionary<string, string> Starter = new Dictionary<string, string>
    {
        ["functions/hello.ts"] = """
            import { defineFunction } from "@dcms/functions";

            /** GET or POST /api/{this app's address}/fn/hello?name=… */
            export default defineFunction({
              description: "Says hello.",
              access: { http: ["public"] },
              handler(input: { name?: string }, ctx) {
                ctx.log.info("hello", input.name ?? "world");
                return { message: `Hello, ${input.name ?? "world"}!` };
              },
            });

            """,
        ["README.md"] = """
            # Functions

            Each file in `functions/` is one function, named by its file: `functions/hello.ts` is `hello`.
            It must `export default defineFunction({ ... })` from `"@dcms/functions"`, and may import other
            files of this repository. Merge into `release` to build; deploy the build from the app's Deployments.

            """,
    };

    private Guid InstanceId => context.Instance?.InstanceId ?? throw new InvalidOperationException("No instance.");

    /// <summary>The instance's repository in this environment, created with the starter on first use.</summary>
    public Task<RepoInfo> RepoAsync(CancellationToken ct) =>
        context.Contracts.Get<IPluginRepos>().EnsureAsync(new RepoSetup("read", "edit", "deploy", Starter), ct);

    public async Task<IReadOnlyList<DeploymentSummary>> ListAsync(CancellationToken ct)
    {
        using var rls = Scope();
        var rows = await db.Deployments.AsNoTracking().Where(d => d.InstanceId == InstanceId)
            .OrderByDescending(d => d.Number).Take(50).ToListAsync(ct);
        return rows.Select(Summary).ToList();
    }

    public async Task<DeploymentDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        using var rls = Scope();
        var row = await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.InstanceId == InstanceId, ct);
        if (row is null) return null;
        var functions = FunctionManifest.Deserialize(row.Manifest);
        return new DeploymentDetail(Summary(row), functions,
            row.Diagnostics is null ? null : JsonDocument.Parse(row.Diagnostics).RootElement.Clone(),
            row.Status == FunctionDeploymentStatus.Ready ? Required(functions) : row.VouchedPermissions);
    }

    /// <summary>Makes a built deployment live (deploy), or an earlier one again (rollback).</summary>
    public async Task<DeploymentSummary?> ActivateAsync(Guid id, CancellationToken ct)
    {
        using var rls = Scope();
        var target = await db.Deployments.FirstOrDefaultAsync(d => d.Id == id && d.InstanceId == InstanceId, ct);
        if (target is null) return null;
        if (target.Status == FunctionDeploymentStatus.Active) return Summary(target);
        if (target.Status is FunctionDeploymentStatus.Building or FunctionDeploymentStatus.Failed)
        {
            throw new ContractConflictException($"Deployment {target.Number} is {target.Status.ToString().ToLowerInvariant()}; only a built one can go live.");
        }

        var required = await VouchAsync(target);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var live = await db.Deployments.FirstOrDefaultAsync(d => d.InstanceId == InstanceId && d.Status == FunctionDeploymentStatus.Active, ct);
        if (live is not null)
        {
            live.Status = target.Number < live.Number ? FunctionDeploymentStatus.RolledBack : FunctionDeploymentStatus.Superseded;
            await db.SaveChangesAsync(ct);
        }
        if (target.Status == FunctionDeploymentStatus.Ready)
        {
            target.VouchedPermissions = [.. required];
        }
        target.Status = FunctionDeploymentStatus.Active;
        target.ActivatedAt = DateTimeOffset.UtcNow;
        target.ActivatedBy = context.Actor.Id?.ToString() ?? context.Actor.Key;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Summary(target);
    }

    /// <summary>Builds the release head again: after a failed build, or when the SDK it was checked against changed.</summary>
    public async Task<JobTicket> RebuildAsync(CancellationToken ct)
    {
        await RepoAsync(ct);
        var head = (await context.Contracts.Get<IPluginRepos>().HeadAsync(new RepoHeadQuery("release"), ct)).Sha
                   ?? throw new ContractConflictException("Nothing has been merged into release yet.");
        using var rls = Scope();
        // A job's dedupe key is permanent: counting this commit's builds lets a failed one be built again.
        var builds = await db.Deployments.CountAsync(d => d.InstanceId == InstanceId && d.CommitSha == head, ct);
        return await BuildJob.EnqueueAsync(context, InstanceId, head, context.Instance?.Slug, ct, attempt: builds);
    }

    /// <summary>
    /// Runs one function now with an input (the editor's Run): the live deployment, or a built one
    /// being tried before it goes live. Either way the caller must hold everything it was or would
    /// be vouched for, as deploying it takes.
    /// </summary>
    public async Task<RunOutcome> RunAsync(string function, JsonNode? input, Guid? deploymentId, CancellationToken ct)
    {
        FunctionDeployment? deployment;
        using (Scope())
        {
            deployment = await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.InstanceId == InstanceId
                && (deploymentId == null ? d.Status == FunctionDeploymentStatus.Active : d.Id == deploymentId), ct);
        }
        if (deployment is null)
        {
            throw new ContractConflictException(deploymentId is null ? "Nothing is deployed yet." : $"There is no deployment {deploymentId}.");
        }
        if (deployment.Status is not (FunctionDeploymentStatus.Active or FunctionDeploymentStatus.Ready))
        {
            throw new ContractConflictException($"Deployment {deployment.Number} is {deployment.Status.ToString().ToLowerInvariant()}; only a built or live one runs.");
        }
        // The caller holds whatever the run may do: a manual run lends nobody the deployer's rights.
        var vouched = await VouchAsync(deployment);
        var target = RunTargets.Of(deployment, function, vouched) ?? throw RunTargets.NoSuchFunction(function);
        return await runner.RunAsync(target, input, new RunTrigger("manual"), ct);
    }

    /// <summary>What making <paramref name="target"/> live needs, if the caller holds all of it (definer's rights).</summary>
    private async Task<List<string>> VouchAsync(FunctionDeployment target)
    {
        var required = target.Status == FunctionDeploymentStatus.Ready
            ? Required(FunctionManifest.Deserialize(target.Manifest))
            : target.VouchedPermissions;
        var missing = new List<string>();
        foreach (var permission in required)
        {
            if (!(await authz.AuthorizeAsync(http.HttpContext!.User, null, new PermissionRequirement(permission))).Succeeded)
            {
                missing.Add(permission);
            }
        }
        return missing.Count > 0 ? throw new DeployRefusedException(missing) : required;
    }

    /// <summary>Everything the functions use, contract operations included.</summary>
    private List<string> Required(IReadOnlyList<FunctionSpec> functions) =>
        [.. FunctionManifest.RequiredPermissions(functions)
            .Concat(functions.SelectMany(f => f.Uses.Contracts).SelectMany(c => HostCalls.ContractPermissions(registry, c)))
            .Distinct().Order(StringComparer.Ordinal)];

    private IDisposable Scope() => RlsScope.Environment(context.TenantId, context.EnvironmentId);

    private static DeploymentSummary Summary(FunctionDeployment d) => new(
        d.Id, d.Number, d.CommitSha, d.Status.ToString(), d.Error,
        FunctionManifest.Deserialize(d.Manifest).Select(f => f.Name).ToList(),
        d.CreatedAt, d.ActivatedAt, d.ActivatedBy);
}
