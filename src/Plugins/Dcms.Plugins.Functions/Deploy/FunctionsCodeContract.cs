using System.Text.Json;
using Dcms.Plugins.Functions.Api;
using Dcms.Plugins.Functions.Build;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Functions.Deploy;

/// <summary>
/// <c>functions.code@1</c>: the deployment service and the repository as contract operations, so
/// the console's assistant — asked from a Dynamic Apps app's page, say — can write, check, test,
/// ship and deploy functions. The dispatcher has checked each operation's permission; deploying
/// still checks everything the functions use against the member (definer's rights).
/// </summary>
internal sealed class FunctionsCodeContract : IFunctionsCode
{
    private readonly IServiceProvider services;
    private readonly IPluginContext context;

    public FunctionsCodeContract(IServiceProvider services, IPluginContext context)
    {
        (this.services, this.context) = (services, context);
        // A dispatched call has no plugin route around it: what this scope resolves as "the
        // plugin's context" (the build, the broker) must be this instance's, as on its routes.
        var accessor = services.GetRequiredService<Dcms.PluginSdk.Runtime.Contracts.PluginContextAccessor>();
        accessor.Current ??= context;
    }

    private const string DefaultBranch = "main";
    private const int MaxReadChars = 512 * 1024;

    private DeploymentService Deployments => ActivatorUtilities.CreateInstance<DeploymentService>(services, context);

    private IPluginRepos Repos => context.Contracts.Get<IPluginRepos>();

    public async Task<CodeOverview> OverviewAsync(CancellationToken ct)
    {
        var deployments = Deployments;
        var list = await deployments.ListAsync(ct);
        var live = list.FirstOrDefault(d => d.Status == "Active");
        var detail = live is null ? null : await deployments.GetAsync(live.Id, ct);
        return new CodeOverview(
            live?.Number,
            (detail?.Functions ?? []).Select(f => new CodeFunction(f.Name, f.Description, JsonSerializer.SerializeToNode(f.Triggers)!,
                f.Http, JsonSerializer.SerializeToNode(f.Uses, JsonSerializerOptions.Web)!)).ToList(),
            list.Take(5).Select(d => new CodeBuild(d.Number, d.Status, d.CommitSha.Length > 7 ? d.CommitSha[..7] : d.CommitSha, d.Error)).ToList());
    }

    public async Task<CodeFiles> ReadFilesAsync(CodeRef input, CancellationToken ct)
    {
        var files = await FilesAsync(input.Branch, ct);
        // Bounded: a reply is a model's context, and a repository is not.
        var shown = new Dictionary<string, string>();
        var total = 0;
        foreach (var (path, content) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            total += content.Length;
            shown[path] = total <= MaxReadChars ? content : "(omitted: the files read so far reached the size limit)";
        }
        return new CodeFiles(shown);
    }

    public async Task<CodeSdk> SdkAsync(CancellationToken ct)
    {
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        var authz = services.GetRequiredService<IAuthorizationService>();
        var typings = await services.GetRequiredService<SdkTypings>().GenerateAsync(ct, http is null ? null : async permission =>
            (await authz.AuthorizeAsync(http.User, null, new PermissionRequirement(permission))).Succeeded);
        return new CodeSdk(typings);
    }

    public async Task<CodeWritten> WriteFilesAsync(CodeWrite input, CancellationToken ct)
    {
        await Deployments.RepoAsync(ct);
        var branch = string.IsNullOrWhiteSpace(input.Branch) ? DefaultBranch : input.Branch.Trim();
        var written = await Repos.CommitAsync(new RepoCommit(branch, input.Message, input.Put, input.Delete), ct);
        return new CodeWritten(written.Sha, branch);
    }

    public async Task<CodeCheck> CheckAsync(CodeRef input, CancellationToken ct)
    {
        var check = await Deployments.CheckAsync(await FilesAsync(input.Branch, ct), ct);
        return new CodeCheck(check.Ok, check.Problems.Select(p => new CodeProblem(p.Message, p.File, p.Line)).ToList(), check.Functions, check.Permissions);
    }

    public async Task<CodeTestResult> TestAsync(CodeTest input, CancellationToken ct)
    {
        var outcome = await Deployments.TestAsync(await FilesAsync(input.Branch, ct), input.Function, input.Input, ct);
        return new CodeTestResult(outcome.Status, outcome.Result, outcome.Error, outcome.Logs);
    }

    public async Task<CodeShipped> ShipAsync(CodeRef input, CancellationToken ct)
    {
        await Deployments.RepoAsync(ct);
        var merged = await Repos.MergeAsync(new RepoMerge(string.IsNullOrWhiteSpace(input.Branch) ? DefaultBranch : input.Branch.Trim()), ct);
        return new CodeShipped(merged.Merged, merged.UpToDate, merged.Sha, merged.Conflicts);
    }

    public async Task<CodeDeployed> DeployAsync(CodeDeploy input, CancellationToken ct)
    {
        var deployments = Deployments;
        var id = await deployments.FindAsync(input.Number, ct)
                 ?? throw new ContractConflictException(input.Number is { } n ? $"There is no deployment {n}." : "No build is waiting to be deployed: ship a branch first.");
        try
        {
            var live = await deployments.ActivateAsync(id, ct) ?? throw new ContractConflictException("That deployment is gone.");
            return new CodeDeployed(live.Number, live.Status);
        }
        catch (DeployRefusedException e)
        {
            throw new ContractValidationException(e.Message);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> FilesAsync(string? branch, CancellationToken ct)
    {
        await Deployments.RepoAsync(ct);
        return (await Repos.ReadFilesAsync(new RepoFilesQuery(string.IsNullOrWhiteSpace(branch) ? DefaultBranch : branch.Trim()), ct)).Files;
    }
}
