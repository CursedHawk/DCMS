using System.Text.Json;
using Dcms.Plugins.Forms;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Forms;

/// <summary>
/// Review surface for visitor form submissions (written by content-api). Gated on
/// content:read / content:write rather than a new permission key so existing
/// tenant roles can reach it without a re-seed.
/// </summary>
public static class FormSubmissionEndpoints
{
    public static IEndpointRouteBuilder MapFormSubmissionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/forms/{instanceId:guid}/submissions", async (
            Guid instanceId, string? formName, int? page, int? pageSize,
            CmsDbContext cms, FormsDbContext db, CancellationToken ct) =>
        {
            var instance = await cms.PluginInstances.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == instanceId && p.PluginId == FormsPlugin.PluginId, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }

            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var current = Math.Max(page ?? 1, 1);

            var query = db.Submissions.AsNoTracking().Where(s => s.PluginInstanceId == instanceId);
            if (!string.IsNullOrWhiteSpace(formName))
            {
                query = query.Where(s => s.FormName == formName);
            }

            var total = await query.LongCountAsync(ct);
            var rows = await query
                .OrderByDescending(s => s.SubmittedAt)
                .Skip((current - 1) * size)
                .Take(size)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                items = rows.Select(s => new
                {
                    id = s.Id,
                    formName = s.FormName,
                    data = JsonDocument.Parse(s.DataJson).RootElement,
                    submittedAt = s.SubmittedAt,
                    handledAt = s.HandledAt,
                }),
                page = current,
                pageSize = size,
                totalCount = total,
            });
        }).RequirePermission(PlatformPermissions.ContentRead);

        app.MapPost("/api/admin/forms/submissions/{id:guid}/handled", async (
            Guid id, FormsDbContext db, CancellationToken ct) =>
        {
            var submission = await db.Submissions.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (submission is null)
            {
                return Results.NotFound();
            }
            submission.HandledAt = submission.HandledAt is null ? DateTimeOffset.UtcNow : null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { handledAt = submission.HandledAt });
        }).RequirePermission(PlatformPermissions.ContentWrite);

        app.MapDelete("/api/admin/forms/submissions/{id:guid}", async (
            Guid id, FormsDbContext db, CancellationToken ct) =>
        {
            var submission = await db.Submissions.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (submission is null)
            {
                return Results.NotFound();
            }
            db.Submissions.Remove(submission);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.ContentWrite);

        return app;
    }
}
