using System.Text.Json;
using Dcms.AdminApi.Sites.Git;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Jobs;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Sites.Audits;

/// <summary>
/// PageSpeed Insights audits of a site (#649), from any of its editors and its agent: of the live
/// site, or of what is not published yet. Testing is editing work (<c>site:edit</c>).
/// </summary>
public static class SiteAuditEndpoints
{
    public sealed record AuditRequest(string? Target, string? Branch);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSiteAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/sites/{id:guid}/audits", async (
            Guid id, AuditRequest body, SitesDbContext db, TenancyDbContext tenancy, CmsDbContext cms, IEventPublisher events,
            SiteGitService git, IJobQueue jobs, PageSpeedClient psi, IConfiguration config, CurrentUser user,
            Dcms.Shared.Data.UserAuth.UserAuthDbContext userAuth, CancellationToken ct) =>
        {
            if (!psi.Configured)
            {
                return Results.Conflict(new
                {
                    error = "PageSpeed Insights is not set up on this platform yet: an operator adds a Google API key (PageSpeed__ApiKey).",
                    code = "audit_not_configured",
                });
            }
            var target = body.Target is "draft" ? "draft" : body.Target is null or "live" ? "live" : null;
            if (target is null)
            {
                return Results.BadRequest(new { error = "target is live or draft." });
            }
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            var since = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);
            if (await db.Audits.AnyAsync(a => a.SiteId == id && a.CreatedAt > since
                    && (a.Status == SiteAuditStatus.Queued || a.Status == SiteAuditStatus.Building || a.Status == SiteAuditStatus.Testing), ct))
            {
                return Results.Conflict(new { error = "An audit of this site is already running.", code = "audit_running" });
            }

            var audit = new SiteAudit
            {
                Id = Guid.NewGuid(), TenantId = site.TenantId, SiteId = site.Id, Target = target, RequestedBy = user.UserId,
            };
            if (target == "live")
            {
                var host = await tenancy.Domains.AsNoTracking()
                    .Where(d => d.SiteId == id && d.Branch == null && d.VerifiedAt != null)
                    .OrderByDescending(d => d.IsPrimary).ThenBy(d => d.CreatedAt)
                    .Select(d => d.Hostname).FirstOrDefaultAsync(ct);
                if (site.ActiveBuildId is null || host is null)
                {
                    return Results.BadRequest(new { error = "This site is not live yet: publish it and give it a domain, or audit the unpublished version." });
                }
                audit.Url = $"https://{host}/";
            }
            else
            {
                // The edge applies a site's sign-in rules by hostname, and an audit build's address has
                // none: a site with any would be served past them for the hour. Its live site can be audited.
                if (await userAuth.Gates.AnyAsync(g => g.SiteId == id && g.Access != Dcms.Shared.Data.UserAuth.GateAccess.Public, ct))
                {
                    return Results.Conflict(new
                    {
                        error = "This site has sign-in rules, which a temporary audit address would bypass, so its unpublished version cannot be audited. Audit the live site instead.",
                        code = "audit_gated",
                    });
                }
                var (build, error) = await SiteAuditBuilds.QueueAsync(site, body.Branch, user.UserId, db, cms, events, git, jobs, ct);
                if (build is null)
                {
                    return Results.BadRequest(new { error });
                }
                audit.BuildId = build.Id;
                audit.Branch = site.RenderMode == SiteRenderMode.StaticFiles ? null : body.Branch ?? site.GitDefaultBranch ?? SiteGitService.DefaultBranch;
                audit.Url = SiteAuditBuilds.UrlFor(build.AuditToken!, config);
            }
            db.Audits.Add(audit);
            await db.SaveChangesAsync(ct);

            var job = await jobs.EnqueueAsync(new JobSpec(
                SiteAuditJobContract.Kind, site.TenantId, new SiteAuditPayload(audit.Id), "jobs.kinds.siteAudit.title", new { site = site.Name },
                RequiredPermission: PlatformPermissions.SiteEdit, DedupeKey: $"site-audit:{audit.Id}", MaxAttempts: 1, Cancelable: false,
                Links: [new JobLink(JobLinkRels.Source, $"/sites/{site.Id}", site.Name)]) { EnvironmentId = site.EnvironmentId }, ct);
            audit.JobId = job.Id;
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/admin/sites/{id}/audits/{audit.Id}", new { auditId = audit.Id, jobId = job.Id });
        }).RequirePermission(PlatformPermissions.SiteEdit).WithAudit(AuditActions.SiteAuditRequested, "site");

        app.MapGet("/api/admin/sites/{id:guid}/audits", async (Guid id, SitesDbContext db, PageSpeedClient psi, CancellationToken ct) =>
        {
            var audits = await db.Audits.AsNoTracking().Where(a => a.SiteId == id)
                .OrderByDescending(a => a.CreatedAt).Take(20).ToListAsync(ct);
            return Results.Ok(new { configured = psi.Configured, audits = audits.Select(a => View(a, full: false)) });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        app.MapGet("/api/admin/sites/{id:guid}/audits/{auditId:guid}", async (Guid id, Guid auditId, SitesDbContext db, CancellationToken ct) =>
        {
            var audit = await db.Audits.AsNoTracking().FirstOrDefaultAsync(a => a.Id == auditId && a.SiteId == id, ct);
            return audit is null ? Results.NotFound() : Results.Ok(View(audit, full: true));
        }).RequirePermission(PlatformPermissions.SiteEdit);

        return app;
    }

    /// <summary>An audit for the console and the agent; the list carries scores only.</summary>
    private static object View(SiteAudit audit, bool full)
    {
        var mobile = Read(audit.MobileJson);
        var desktop = Read(audit.DesktopJson);
        return new
        {
            id = audit.Id,
            target = audit.Target,
            branch = audit.Branch,
            url = audit.Target == "live" ? audit.Url : null,
            status = audit.Status.ToString(),
            error = audit.Error,
            jobId = audit.JobId,
            createdAt = audit.CreatedAt,
            completedAt = audit.CompletedAt,
            scores = new { mobile = mobile?.Scores, desktop = desktop?.Scores },
            mobile = full ? mobile : null,
            desktop = full ? desktop : null,
        };
    }

    private static AuditReport? Read(string? json) => json is null ? null : JsonSerializer.Deserialize<AuditReport>(json, Json);
}
