using System.Text.Json;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Sites.Audits;

/// <summary>
/// Runs one audit (#649) on the jobs-worker: waits for a draft's audit build, then asks PageSpeed
/// Insights for mobile and desktop and keeps each summary on the <see cref="SiteAudit"/>.
/// </summary>
public sealed class SiteAuditJob(SitesDbContext db, PageSpeedClient psi) : IJobHandler<SiteAuditPayload>
{
    public const string Kind = SiteAuditJobContract.Kind;

    private static readonly TimeSpan BuildWait = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<object?> RunAsync(SiteAuditPayload payload, IJobContext context)
    {
        var ct = context.Cancellation;
        var audit = await db.Audits.IgnoreTenantFilter().FirstOrDefaultAsync(a => a.Id == payload.AuditId && a.TenantId == context.TenantId, ct)
            ?? throw new JobFailedException("sites.audit.errors.gone");
        try
        {
            if (audit.BuildId is { } buildId)
            {
                await StageAsync(audit, SiteAuditStatus.Building, 0, "jobs.stages.auditBuilding", context);
                await WaitForBuildAsync(buildId, audit.TenantId, ct);
            }
            await StageAsync(audit, SiteAuditStatus.Testing, 1, "jobs.stages.auditMobile", context);
            var mobile = await RunAsync(audit.Url!, "mobile", ct);
            audit.MobileJson = JsonSerializer.Serialize(mobile, Json);
            await StageAsync(audit, SiteAuditStatus.Testing, 2, "jobs.stages.auditDesktop", context);
            var desktop = await RunAsync(audit.Url!, "desktop", ct);
            audit.DesktopJson = JsonSerializer.Serialize(desktop, Json);
            audit.Status = SiteAuditStatus.Succeeded;
            audit.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return new { auditId = audit.Id, mobile = mobile.Scores, desktop = desktop.Scores };
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            audit.Status = SiteAuditStatus.Failed;
            audit.Error = Clip(e is JobFailedException f ? f.Detail ?? e.Message : e.Message);
            audit.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw e as JobFailedException ?? new JobFailedException("sites.audit.errors.failed", audit.Error, e);
        }
    }

    private async Task<AuditReport> RunAsync(string url, string strategy, CancellationToken ct)
    {
        using var result = await psi.RunAsync(url, strategy, ct);
        return AuditSummary.From(result.RootElement, strategy);
    }

    private async Task StageAsync(SiteAudit audit, SiteAuditStatus status, long done, string stage, IJobContext context)
    {
        audit.Status = status;
        await db.SaveChangesAsync(context.Cancellation);
        await context.ReportAsync(done, 3, stage);
    }

    /// <summary>site-builder builds it on its own lane; this only watches the row.</summary>
    private async Task WaitForBuildAsync(Guid buildId, Guid tenantId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + BuildWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var build = await db.Builds.IgnoreTenantFilter().AsNoTracking()
                .Where(b => b.Id == buildId && b.TenantId == tenantId)
                .Select(b => new { b.Status, b.Error }).FirstOrDefaultAsync(ct)
                ?? throw new JobFailedException("sites.audit.errors.buildGone");
            if (build.Status == SiteBuildStatus.Succeeded) return;
            if (build.Status == SiteBuildStatus.Failed)
            {
                throw new JobFailedException("sites.audit.errors.buildFailed", $"The site did not build: {build.Error}");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new JobFailedException("sites.audit.errors.buildSlow", "The site did not finish building in time.");
    }

    private static string? Clip(string? value) => value is null ? null : value.Length <= 2000 ? value : value[..2000] + "…";
}
