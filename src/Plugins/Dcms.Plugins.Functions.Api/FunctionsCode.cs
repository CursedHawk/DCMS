using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Functions.Api;

public sealed record CodeFunction(string Name, string? Description, JsonNode Triggers, IReadOnlyList<string> Http, JsonNode Uses);

/// <param name="Live">The live deployment's number; null when nothing is deployed.</param>
/// <param name="Builds">The latest builds, newest first: number, status and the first problem of a failed one.</param>
public sealed record CodeOverview(int? Live, IReadOnlyList<CodeFunction> Functions, IReadOnlyList<CodeBuild> Builds);

public sealed record CodeBuild(int Number, string Status, string Commit, string? Error);

/// <param name="Branch">A branch of the app's repository; <c>main</c> when omitted.</param>
public sealed record CodeRef(string? Branch = null);

public sealed record CodeFiles(IReadOnlyDictionary<string, string> Files);

public sealed record CodeSdk(string Typings);

/// <param name="Branch">The working branch to commit to (<c>main</c> when omitted); never <c>release</c>.</param>
/// <param name="Put">Files to write, by path: <c>functions/{name}.ts</c> is the function <c>{name}</c>.</param>
public sealed record CodeWrite(string Message, IReadOnlyDictionary<string, string>? Put = null, IReadOnlyList<string>? Delete = null, string? Branch = null);

public sealed record CodeWritten(string? Sha, string Branch);

public sealed record CodeProblem(string Message, string? File = null, int? Line = null);

/// <param name="Permissions">What deploying this would need its deployer to hold.</param>
public sealed record CodeCheck(bool Ok, IReadOnlyList<CodeProblem> Problems, IReadOnlyList<string> Functions, IReadOnlyList<string> Permissions);

public sealed record CodeShipped(bool Merged, bool UpToDate, string? Sha, IReadOnlyList<string> Conflicts);

/// <param name="Number">The build to make live; the latest ready one when omitted.</param>
public sealed record CodeDeploy(int? Number = null);

public sealed record CodeDeployed(int Number, string Status);

/// <param name="Branch">Whose code to run, built now; <c>main</c> when omitted.</param>
public sealed record CodeTest(string Function, JsonNode? Input = null, string? Branch = null);

public sealed record CodeTestResult(string Status, JsonNode? Result, string? Error, string Logs);

/// <summary>
/// A Functions app's code for the console's assistant and other plugins (ADR 0029): what is
/// live, the repository's files, the SDK, and the way from a change to a deployment — write to a
/// branch, check it, ship it to release (which builds), deploy the build. Each step needs its
/// own permission, and deploying needs everything the functions use (definer's rights).
/// </summary>
[DcmsContract("functions.code", 1, Description = "Write, check, test and deploy a Functions app's TypeScript functions.")]
public interface IFunctionsCode
{
    [Operation(OpRisk.Read, Permission = FunctionsPermissions.Read, Expose = OpExposure.Admin | OpExposure.Ai, ReturnsExternalText = true,
        Description = "Start here. What is live (each function's triggers, HTTP access and uses) and the latest builds with their status.")]
    Task<CodeOverview> OverviewAsync(CancellationToken ct);

    [Operation(OpRisk.Read, Permission = FunctionsPermissions.Read, Expose = OpExposure.Admin | OpExposure.Ai, ReturnsExternalText = true,
        Description = "Every file of a branch of the app's repository (main by default). Each functions/{name}.ts is one function: "
            + "it must `export default defineFunction({...})` from \"@dcms/functions\" and may import other files of the repository, nothing else.")]
    Task<CodeFiles> ReadFilesAsync(CodeRef input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = FunctionsPermissions.Read, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "The TypeScript declarations of \"@dcms/functions\" for this environment: defineFunction, the triggers (on.row, on.message, "
            + "on.schedule, …), `uses` (the capability grant: tables and verbs, contracts, egress hosts, secrets, messages) and the context: "
            + "ctx.db.<app>.<table> typed from the published tables, ctx.plugins, ctx.http.fetch, ctx.secrets, ctx.messages, ctx.jobs, ctx.log. Read it before writing code.")]
    Task<CodeSdk> SdkAsync(CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = FunctionsPermissions.Edit, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Commit files to a working branch (main by default; never release). Nothing runs until it is shipped and deployed. Check it afterwards.")]
    Task<CodeWritten> WriteFilesAsync(CodeWrite input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = FunctionsPermissions.Edit, Expose = OpExposure.Admin | OpExposure.Ai, ReturnsExternalText = true,
        Description = "Type-check and build a branch without deploying it: every problem with its file and line, the functions it defines, "
            + "and the permissions deploying it would take. Fix what it reports before shipping.")]
    Task<CodeCheck> CheckAsync(CodeRef input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = FunctionsPermissions.Deploy, Expose = OpExposure.Admin | OpExposure.Ai, ReturnsExternalText = true,
        Description = "Run one function of a branch now, built on the spot, with an input. It runs for real, with your permissions: its writes, "
            + "messages and HTTP calls happen. Say what it will do first.")]
    Task<CodeTestResult> TestAsync(CodeTest input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = FunctionsPermissions.Deploy, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Merge a branch into release, which builds it into a deployment (not yet live: deploy it next). Conflicts are returned, not merged.")]
    Task<CodeShipped> ShipAsync(CodeRef input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = FunctionsPermissions.Deploy, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Make a built deployment live (the latest ready one by default), or an earlier one again. Its functions start running "
            + "on their triggers. You must hold every permission they use; say which functions go live and what they do first.")]
    Task<CodeDeployed> DeployAsync(CodeDeploy input, CancellationToken ct);
}
