using System.Text.Json;
using Dcms.Shared.Audit;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Environments;

/// <summary>
/// One step of making a new environment: a kind of configuration (<c>kind:sites</c>) or a scope of
/// data (<c>data:dynamic-apps-records</c>), how far it got and what it is on now.
/// </summary>
/// <param name="State"><c>waiting</c>, <c>running</c> or <c>done</c>.</param>
/// <param name="Item">What it is copying right now: a site's name, a role, an app.</param>
public sealed record SetupStep(string Key, string State, int Done, int? Total, string? Item);

/// <summary>
/// The steps of a copy as the environment being made shows them (its <c>SetupJson</c>), written as
/// the copy goes -- at most every half second, and at once whenever a step starts or ends -- so
/// whoever waits sees exactly what is being copied. The job's own progress follows the same count.
/// </summary>
public sealed class EnvironmentSetupLog(TenancyDbContext db, AuditScope audit, TenantEnvironment environment, IJobContext job, List<SetupStep> steps)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset _written = DateTimeOffset.MinValue;

    public static EnvironmentSetupLog Start(TenancyDbContext db, AuditScope audit, TenantEnvironment environment, IJobContext job,
        IEnumerable<string> kinds, IEnumerable<string> scopes) =>
        new(db, audit, environment, job,
            [.. kinds.Select(k => new SetupStep($"kind:{k}", "waiting", 0, null, null)),
             .. scopes.Select(s => new SetupStep($"data:{s}", "waiting", 0, null, null))]);

    /// <summary>A kind's item about to be copied (<paramref name="done"/> of <paramref name="total"/> done); null item when the kind is finished.</summary>
    public Task KindAsync(string kind, int done, int total, string? item) =>
        SetAsync($"kind:{kind}", item is null ? "done" : "running", done, total, item);

    /// <summary>A data scope starting (no rows yet) or finished (rows copied).</summary>
    public Task ScopeAsync(string scope, int? rows) =>
        SetAsync($"data:{scope}", rows is null ? "running" : "done", rows ?? 0, null, null);

    private async Task SetAsync(string key, string state, int done, int? total, string? item)
    {
        var at = steps.FindIndex(s => s.Key == key);
        if (at < 0) return;
        var changedState = steps[at].State != state;
        steps[at] = new SetupStep(key, state, done, total, item);
        var finished = steps.Count(s => s.State == "done");
        await job.ReportAsync(finished, steps.Count, "jobs.stages.environmentCopy.configuration");
        if (!changedState && DateTimeOffset.UtcNow - _written < TimeSpan.FromMilliseconds(500)) return;
        await WriteAsync(ready: false);
    }

    /// <summary>The log as it stands; with <paramref name="ready"/>, the environment opens.</summary>
    public async Task WriteAsync(bool ready)
    {
        _written = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(steps, Json);
        DateTimeOffset? readyAt = ready ? DateTimeOffset.UtcNow : null;
        // rls: platform -- the environment row by its id and tenant, from the job, outside any request.
        using var rls = RlsScope.Platform();
        // Progress, written twice a second: the creation is audited (environment.created), and what
        // the copy writes is recorded by each kind as it writes it. A record per tick says nothing.
        using var quiet = audit.SuppressBulkCapture();
        await db.Environments.IgnoreQueryFilters()
            .Where(e => e.TenantId == environment.TenantId && e.Id == environment.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.SetupJson, json).SetProperty(e => e.ReadyAt, e => readyAt ?? e.ReadyAt), CancellationToken.None);
    }
}
