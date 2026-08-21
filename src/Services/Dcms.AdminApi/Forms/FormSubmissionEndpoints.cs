using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
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
        // The forms an operator can review: every Forms plugin instance with the
        // forms declared in its config, so the admin can render real labels and
        // columns instead of raw payload keys.
        app.MapGet("/api/admin/forms", async (
            CmsDbContext cms, FormsDbContext db, CancellationToken ct) =>
        {
            var instances = await cms.PluginInstances.AsNoTracking()
                .Where(p => p.PluginId == FormsPlugin.PluginId)
                .OrderBy(p => p.Name)
                .ToListAsync(ct);

            var counts = await db.Submissions.AsNoTracking()
                .GroupBy(s => new { s.PluginInstanceId, s.FormName })
                .Select(g => new
                {
                    g.Key.PluginInstanceId,
                    g.Key.FormName,
                    Total = g.LongCount(),
                    Unhandled = g.LongCount(s => s.HandledAt == null),
                })
                .ToListAsync(ct);

            var countLookup = counts.ToDictionary(c => (c.PluginInstanceId, c.FormName));

            // Materialised rather than deferred: each instance's config document is
            // disposed as soon as its forms are read.
            var result = new List<object>(instances.Count);
            foreach (var instance in instances)
            {
                using var config = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(instance.ConfigJson) ? "{}" : instance.ConfigJson);

                var forms = FormsPlugin.ReadForms(config).Select(form =>
                {
                    countLookup.TryGetValue((instance.Id, form.Name), out var count);
                    return new
                    {
                        name = form.Name,
                        title = form.Title ?? form.Name,
                        fields = form.Fields.Select(f => new
                        {
                            name = f.Name,
                            label = f.Label ?? f.Name,
                            type = f.Type,
                            required = f.Required,
                        }).ToList(),
                        // Empty when the form has notifications off or no recipients.
                        notifyRecipients = form.Notify?.Recipients ?? (IReadOnlyList<string>)[],
                        totalCount = count?.Total ?? 0,
                        unhandledCount = count?.Unhandled ?? 0,
                    };
                }).ToList();

                result.Add(new
                {
                    instanceId = instance.Id,
                    slug = instance.Slug,
                    name = instance.Name,
                    enabled = instance.Enabled,
                    forms,
                });
            }

            return Results.Ok(result);
        }).RequirePermission(PlatformPermissions.ContentRead);

        app.MapGet("/api/admin/forms/{instanceId:guid}/submissions", async (
            Guid instanceId, string? formName, string? handled, int? page, int? pageSize,
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
            query = handled switch
            {
                "unhandled" => query.Where(s => s.HandledAt == null),
                "handled" => query.Where(s => s.HandledAt != null),
                _ => query,
            };

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
                    userAgent = s.UserAgent,
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
        }).RequirePermission(PlatformPermissions.ContentWrite).WithAudit(AuditActions.FormSubmissionHandled, "form_submission");

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
        }).RequirePermission(PlatformPermissions.ContentWrite).WithAudit(AuditActions.FormSubmissionDeleted, "form_submission");

        return app;
    }
}
