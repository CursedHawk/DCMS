using System.Text.Json;
using Dcms.Plugins.Functions.Build;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Functions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.Functions.Deploy;

/// <summary>A commit that does not build: the first problem, where it is.</summary>
public sealed class BuildFailedException(string message) : Exception(message);

/// <summary>
/// Making a commit live without a person at the console: what an environment deployment and the
/// flows' migration share. The caller has decided what the deployment is vouched for.
/// </summary>
// rls: callers enter the instance's RlsScope.Environment; IgnoreTenantFilter keeps it there.
internal static class Deployer
{
    /// <summary>The commit's Ready deployment, or a new one built now.</summary>
    /// <exception cref="BuildFailedException">It does not build; the deployment is left Failed with every problem.</exception>
    public static async Task<FunctionDeployment> BuiltAsync(
        FunctionsDbContext db, IPluginContext plugin, Builder builder, string sha, string by, CancellationToken ct)
    {
        var instanceId = plugin.Instance!.InstanceId;
        var deployment = await db.Deployments.IgnoreTenantFilter().FirstOrDefaultAsync(d =>
            d.InstanceId == instanceId && d.CommitSha == sha && d.Status == FunctionDeploymentStatus.Ready, ct);
        if (deployment is not null) return deployment;

        // Claimed before it is built, so the push's build finds it there and stops.
        // ponytail: a push build already under way leaves a second deployment of the commit; harmless.
        deployment = new FunctionDeployment
        {
            Id = Guid.CreateVersion7(),
            TenantId = plugin.TenantId,
            EnvironmentId = plugin.EnvironmentId,
            InstanceId = instanceId,
            Number = (await db.Deployments.IgnoreTenantFilter().Where(d => d.InstanceId == instanceId).MaxAsync(d => (int?)d.Number, ct) ?? 0) + 1,
            CommitSha = sha,
            CreatedBy = by,
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(ct);

        var files = await plugin.Contracts.Get<IPluginRepos>().ReadFilesAsync(new RepoFilesQuery(sha), ct);
        var built = await builder.BuildAsync(files.Files, FunctionsPlugin.PackagesAllowed(plugin.Instance), ct);
        if (!built.Ok)
        {
            var first = built.Diagnostics.FirstOrDefault();
            deployment.Status = FunctionDeploymentStatus.Failed;
            deployment.Diagnostics = JsonSerializer.Serialize(built.Diagnostics.Take(200), JsonSerializerOptions.Web);
            deployment.Error = first?.Message is { } m ? m[..Math.Min(m.Length, 4000)] : "The build failed.";
            await db.SaveChangesAsync(ct);
            throw new BuildFailedException(first is null ? "the build failed" : first.File is null ? first.Message : $"{first.File}:{first.Line}: {first.Message}");
        }
        deployment.Manifest = FunctionManifest.Serialize(built.Functions);
        deployment.Bundles = JsonSerializer.Serialize(built.Bundles);
        deployment.Packages = [.. built.Packages ?? []];
        deployment.Status = FunctionDeploymentStatus.Ready;
        await db.SaveChangesAsync(ct);
        return deployment;
    }

    /// <summary>Makes a Ready deployment the live one, vouched for <paramref name="vouched"/>.</summary>
    public static async Task ActivateAsync(FunctionsDbContext db, FunctionDeployment deployment, List<string> vouched, string by, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var live = await db.Deployments.IgnoreTenantFilter().FirstOrDefaultAsync(d =>
            d.InstanceId == deployment.InstanceId && d.Status == FunctionDeploymentStatus.Active && d.Id != deployment.Id, ct);
        if (live is not null)
        {
            live.Status = FunctionDeploymentStatus.Superseded;
            await db.SaveChangesAsync(ct);
        }
        deployment.VouchedPermissions = vouched;
        deployment.Status = FunctionDeploymentStatus.Active;
        deployment.ActivatedAt = DateTimeOffset.UtcNow;
        deployment.ActivatedBy = by;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
