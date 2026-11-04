using Dcms.Plugins.Functions.Build;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.Functions.Deploy;

/// <summary>
/// The Functions side of retiring Dynamic Apps flows (ADR 0029, F11): their code, written as
/// functions and committed by the platform, checked and made live. Nobody deploys it at the
/// console, so it is vouched for what the flows could already do — the platform says what that
/// is — and for nothing more: a call needing anything else is refused at run time, as the flow's
/// step would have been.
/// </summary>
public sealed class FlowImport(FunctionsDbContext db, IPluginContext context, PluginRegistry registry, Builder builder)
{
    public const string By = "flows";

    /// <param name="Problems">Every problem building the files has; empty when they build.</param>
    /// <param name="Required">What deploying each function needs vouched, by function.</param>
    public sealed record Checked(IReadOnlyList<string> Problems, IReadOnlyDictionary<string, IReadOnlyList<string>> Required);

    public async Task<Checked> CheckAsync(IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        var built = await builder.BuildAsync(files, ct);
        return built.Ok
            ? new Checked([], built.Functions.ToDictionary(f => f.Name, f => (IReadOnlyList<string>)DeploymentService.Required(registry, [f])))
            : new Checked(built.Diagnostics.Select(d => d.File is null ? d.Message : $"{d.File}:{d.Line}: {d.Message}").ToList(), new Dictionary<string, IReadOnlyList<string>>());
    }

    /// <summary>Builds <paramref name="sha"/> and makes it live, vouched for what it uses of <paramref name="allowed"/>.</summary>
    /// <exception cref="BuildFailedException">It does not build.</exception>
    public async Task<int> DeployAsync(string sha, IReadOnlySet<string> allowed, CancellationToken ct)
    {
        using var rls = RlsScope.Environment(context.TenantId, context.EnvironmentId);
        var live = await db.Deployments.IgnoreTenantFilter().AsNoTracking().FirstOrDefaultAsync(d =>
            d.InstanceId == context.Instance!.InstanceId && d.Status == FunctionDeploymentStatus.Active, ct);
        if (live?.CommitSha == sha) return live.Number;

        var deployment = await Deployer.BuiltAsync(db, context, builder, sha, By, ct);
        var vouched = DeploymentService.Required(registry, FunctionManifest.Deserialize(deployment.Manifest)).Where(allowed.Contains).ToList();
        await Deployer.ActivateAsync(db, deployment, vouched, By, ct);
        return deployment.Number;
    }
}
