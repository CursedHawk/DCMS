using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.Functions.Api;
using Dcms.Plugins.Functions.Deploy;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Audit;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.Functions.Runtime;

/// <summary>
/// Retires Dynamic Apps flows into Functions (ADR 0029, F11), by itself, in every environment
/// that still has any. An environment's flows become the functions of one Functions app,
/// <c>automations</c>, which this plugin installs for the purpose (<c>dcms.instances@1</c>):
/// <list type="number">
/// <item>Each app writes its published flows as code (<c>dynamic-apps.automation@1</c>), and the
/// result must build.</item>
/// <item>The apps' flows are switched off.</item>
/// <item>The code is committed on a branch of its own from <c>release</c>, merged, and made
/// live.</item>
/// </list>
/// A failure after step 2 switches the flows back on. There is no moment in which both run: an
/// event in the seconds between the two starts neither, the safer way to be wrong for code that
/// sends email.
///
/// <para>Nobody deploys it at the console, so the deployment is vouched for what the flows could
/// already do: their records, the platform's email, notifications and content, visitor reads,
/// and what their revision's publisher was checked for. Nothing more. And only code this wrote
/// goes live that way: an <c>automations</c> app holding anything else gets the converted flows
/// on a branch for a person to review and deploy, and the flows keep running until then.</para>
/// </summary>
public sealed class FlowMigration(IServiceProvider services, IConfiguration configuration, ILogger<FlowMigration> logger) : BackgroundService
{
    public const string Slug = "automations";
    private const string ReviewBranch = "flows-for-review";

    private static readonly string[] Implicit =
    [
        "plugin:dynamic-apps:data-read", "plugin:dynamic-apps:data-write", "plugin:dynamic-apps:data-delete",
        // A flow on revision.published was told of each publish: the model, which needed nothing.
        "plugin:dynamic-apps:model-read",
        FunctionsPermissions.Deploy, Dcms.Shared.Security.PlatformPermissions.ContentRead,
        // visitor.lookup was a built-in action: it needed nothing.
        Dcms.Plugins.VisitorAuth.Api.VisitorPermissions.Read,
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Functions:MigrateFlows", true)) return;
        // After startup's schema migrations; then now and again, for an environment a deployment
        // or a copy has just given flows.
        await Task.Delay(TimeSpan.FromSeconds(configuration.GetValue("Functions:MigrateFlowsDelaySeconds", 60)), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Moving flows to functions failed; retrying next pass.");
            }
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    /// <summary>One pass over every environment with Dynamic Apps; how many environments' flows moved.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString)) return 0;
        await using var leadership = await PostgresAdvisoryLock.TryAcquireAsync(connectionString, PostgresAdvisoryLock.FlowsToFunctionsLockKey, logger, ct);
        if (leadership is null) return 0;

        List<(Guid Tenant, Guid Environment)> environments;
        using (RlsScope.Platform())
        using (var scope = services.CreateScope())
        {
            // rls: platform -- which environments have Dynamic Apps at all; each is then asked in its own scope.
            // ponytail: asks every app each pass (one query when it has no flows); index on live flows if that grows.
            var rows = await scope.ServiceProvider.GetRequiredService<CmsDbContext>().PluginInstances.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.PluginId == "dynamic-apps" && p.Enabled)
                .Select(p => new { p.TenantId, p.EnvironmentId }).Distinct().ToListAsync(ct);
            environments = rows.Select(r => (r.TenantId, r.EnvironmentId)).ToList();
        }
        var moved = 0;
        foreach (var (tenant, environment) in environments)
        {
            try
            {
                if (await MoveAsync(tenant, environment, ct)) moved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Moving the flows of tenant {Tenant} environment {Environment} to functions failed.", tenant, environment);
            }
        }
        return moved;
    }

    private sealed record App(Guid Instance, string Slug, FlowsExported Export);

    private async Task<bool> MoveAsync(Guid tenant, Guid environment, CancellationToken ct)
    {
        using var rls = RlsScope.Environment(tenant, environment);

        // 1. The code, app by app; an app whose flows cannot be written as functions keeps them.
        var apps = new List<App>();
        InstanceRef target;
        using (var scope = services.CreateScope())
        {
            var plugin = await ContextAsync(scope.ServiceProvider, tenant, null, ct);
            var enabled = await scope.ServiceProvider.GetRequiredService<PluginContextFactory>().EnabledInstancesAsync(tenant, ct);
            var request = new FlowsExport(
                enabled.FirstOrDefault(i => i.PluginId == "user-auth")?.Slug, enabled.FirstOrDefault(i => i.PluginId == "visitor-auth")?.Slug, []);
            var taken = new List<string>();
            foreach (var app in enabled.Where(i => i.PluginId == "dynamic-apps").OrderBy(i => i.Slug, StringComparer.Ordinal))
            {
                try
                {
                    var export = await plugin.Contracts.Get<IDynamicAppsAutomation>(app.InstanceId).ExportFlowsAsync(request with { Taken = taken }, ct);
                    if (export is null) continue;
                    taken.AddRange(export.Functions.Values);
                    apps.Add(new App(app.InstanceId, app.Slug, export));
                }
                catch (ContractValidationException e)
                {
                    logger.LogWarning("App {App} of tenant {Tenant} keeps its flows: {Reason}", app.Slug, tenant, e.Message);
                }
            }
            if (apps.Count == 0) return false;
            target = await plugin.Contracts.Get<IPluginInstances>().EnsureOwnAsync(
                new InstanceEnsure(Slug, "Automations", "Your Dynamic Apps flows, written as functions when flows were retired."), ct);
        }

        // A new scope: the one above read the enabled instances before the app was installed.
        using var work = services.CreateScope();
        var sp = work.ServiceProvider;
        var instance = await sp.GetRequiredService<PluginContextFactory>().FindAnyInstanceAsync(tenant, target.Slug, ct)
                       ?? throw new InvalidOperationException($"The {target.Slug} app is not there after installing it.");
        var functions = await ContextAsync(sp, tenant, instance, ct);
        var repos = functions.Contracts.Get<IPluginRepos>();
        var repo = await repos.EnsureAsync(new RepoSetup("read", "edit", "deploy", new Dictionary<string, string> { ["README.md"] = Readme }), ct);
        var import = ActivatorUtilities.CreateInstance<FlowImport>(sp, functions);

        // Each app's functions may need only what that app's flows could do: the implicit set and
        // what its own publisher was checked for. The deployment's vouching is one set for all of
        // them, so an app that would need more than its own keeps its flows, and cannot borrow
        // another app's through it. A function reaches only what its `uses` declares, which the
        // check below is over, so the shared set gives no function more than its own app's.
        Dictionary<string, string> files;
        while (true)
        {
            files = Files(apps);
            var check = await import.CheckAsync(files, ct);
            if (check.Problems.Count > 0)
            {
                logger.LogWarning("The flows of tenant {Tenant} environment {Environment} do not build as functions: {Problems}",
                    tenant, environment, string.Join("; ", check.Problems.Take(5)));
                return false;
            }
            var beyond = apps.Select(a => (App: a, Missing: a.Export.Functions.Values
                    .SelectMany(f => check.Required.GetValueOrDefault(f) ?? []).Where(p => !Allowed(a).Contains(p)).Distinct().ToList()))
                .Where(x => x.Missing.Count > 0).ToList();
            if (beyond.Count == 0) break;
            foreach (var (app, missing) in beyond)
            {
                logger.LogWarning("App {App} of tenant {Tenant} keeps its flows: as functions they need {Missing}, which its publisher was not checked for.",
                    app.Slug, tenant, string.Join(", ", missing));
                apps.Remove(app);
            }
            if (apps.Count == 0) return false;
        }

        // Only code the platform wrote goes live this way, vouched as the flows were.
        // ponytail: so a second app's flows arriving after the first moved land on the branch too; regenerate the moved apps' files if that becomes common.
        var released = (await repos.ReadFilesAsync(new RepoFilesQuery("release"), ct)).Files;
        if (released.Keys.Except(files.Keys).Any())
        {
            await repos.CommitAsync(new RepoCommit(ReviewBranch, "Flows as functions, for review", files, From: "release"), ct);
            logger.LogWarning("Tenant {Tenant} environment {Environment}: its {App} app holds code of its own, so the converted flows are on branch {Branch} for a person to deploy.",
                tenant, environment, target.Slug, ReviewBranch);
            return false;
        }

        // 2. The flows off; 3. the functions live, or the flows back on.
        await RetireAsync(functions, apps, retired: true, ct);
        try
        {
            var names = string.Join(", ", apps.Select(a => a.Slug));
            // A branch nobody can have prepared, from release, so nothing on main rides along.
            var branch = $"flows-{Guid.NewGuid():N}";
            await repos.CommitAsync(new RepoCommit(branch, $"Flows of {names}, as functions", files, From: "release"), ct);
            var merged = await repos.MergeAsync(new RepoMerge(branch, "release"), ct);
            if (merged.Conflicts.Count > 0)
            {
                throw new InvalidOperationException($"release conflicts with the converted flows: {string.Join(", ", merged.Conflicts)}");
            }
            var sha = merged.Sha ?? (await repos.HeadAsync(new RepoHeadQuery("release"), ct)).Sha
                      ?? throw new InvalidOperationException("release has no head after the merge.");
            // What goes live is exactly what this wrote, whatever reached release meanwhile: the
            // commit is immutable, and the build reads it, not the branch.
            var live = (await repos.ReadFilesAsync(new RepoFilesQuery(sha), ct)).Files;
            if (live.Count != files.Count || files.Any(f => !live.TryGetValue(f.Key, out var text) || text != f.Value))
            {
                throw new InvalidOperationException("release holds more than the converted flows; they are left for a person to deploy.");
            }
            var allowed = apps.SelectMany(Allowed).ToHashSet(StringComparer.Ordinal);
            var number = await import.DeployAsync(sha, allowed, ct);
            // The editor opens main: the code arrives there too (best effort; release is what runs).
            try { await repos.MergeAsync(new RepoMerge("release", repo.DefaultBranch), ct); }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogInformation(e, "Could not bring the converted flows onto main."); }

            var audit = sp.GetRequiredService<IAuditRecorder>();
            audit.Record($"plugin.{FunctionsPlugin.PluginId}.flows.migrated")
                .For("plugin_instance", target.Id, target.Slug)
                .With("apps", names)
                .With("functions", apps.Sum(a => a.Export.Functions.Count))
                .With("deployment", number);
            await audit.FlushAsync(ct);
            logger.LogInformation("Moved the flows of {Apps} (tenant {Tenant}) to functions, deployment {Deployment}.", names, tenant, number);
            return true;
        }
        catch
        {
            await RetireAsync(functions, apps, retired: false, CancellationToken.None);
            throw;
        }
    }

    /// <summary>The repository the apps' flows make; the helpers each app ships are the same file.</summary>
    private static Dictionary<string, string> Files(IEnumerable<App> apps)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["README.md"] = Readme };
        foreach (var (path, text) in apps.SelectMany(a => a.Export.Files)) files[path] = text;
        return files;
    }

    /// <summary>What an app's functions may be vouched for: what its flows could do.</summary>
    private static HashSet<string> Allowed(App app)
    {
        var allowed = Implicit.Concat(app.Export.Vouched).ToHashSet(StringComparer.Ordinal);
        if (allowed.Contains(Dcms.Plugins.UserAuth.Api.UserAuthPermissions.UsersManage))
        {
            // The provider looked users and groups up itself; written out, the function does.
            allowed.Add(Dcms.Plugins.UserAuth.Api.UserAuthPermissions.UsersRead);
        }
        return allowed;
    }

    private static async Task RetireAsync(IPluginContext plugin, IEnumerable<App> apps, bool retired, CancellationToken ct)
    {
        foreach (var app in apps)
        {
            await plugin.Contracts.Get<IDynamicAppsAutomation>(app.Instance).RetireFlowsAsync(new FlowsRetire(retired), ct);
        }
    }

    /// <summary>This plugin's context in the scope, as a job of it would have.</summary>
    private static async Task<IPluginContext> ContextAsync(IServiceProvider sp, Guid tenant, PluginInstanceContext? instance, CancellationToken ct)
    {
        var context = await sp.GetRequiredService<PluginContextFactory>().CreateAsync(tenant, FunctionsPlugin.PluginId, instance, PluginActor.System, ct);
        sp.GetRequiredService<PluginContextAccessor>().Current = context;
        return context;
    }

    private const string Readme = """
        # Automations

        Your Dynamic Apps flows, written as functions when flows were retired: one file in
        `functions/` per flow, named after its app and flow, with the same trigger, condition and
        steps. `lib/flow.ts` keeps the flow language's rules (a missing field reads as null, and so
        on), so each function does what its flow did.

        Edit them as any function: merge into `release` to build, then deploy from Deployments.
        Each starts with `// @ts-nocheck`; remove it to have the editor check the file's types.

        """;
}
