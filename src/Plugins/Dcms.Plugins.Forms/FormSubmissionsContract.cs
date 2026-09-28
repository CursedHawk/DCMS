using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.Forms;

public sealed record SubmissionSummary(
    Guid Id,
    Guid InstanceId,
    string FormName,
    IReadOnlyDictionary<string, JsonElement> Data,
    Guid? VisitorId,
    DateTimeOffset SubmittedAt,
    bool Handled);

/// <param name="FormName">Limit to one form; all forms of the instance otherwise.</param>
public sealed record SubmissionQuery(string? FormName = null, int Page = 1, int PageSize = 20);

public sealed record SubmissionPage(IReadOnlyList<SubmissionSummary> Items, int Page, int PageSize, long TotalCount);

public sealed record SubmissionRef(Guid SubmissionId);

/// <summary>A form was submitted and stored. The payload is fetched through the contract, not carried here.</summary>
[ContractEvent("form.submitted")]
public sealed record FormSubmitted(Guid SubmissionId, Guid InstanceId, string FormName, Guid? VisitorId) : IPluginEvent;

/// <summary>Read access to a Forms instance's stored submissions.</summary>
[DcmsContract("forms.submissions", 1,
    Description = "Stored submissions of a Forms instance's forms.",
    Events = [typeof(FormSubmitted)])]
public interface IFormSubmissions
{
    [Operation(OpRisk.Read, Permission = FormsPlugin.SubmissionsReadPermission, Expose = OpExposure.Admin | OpExposure.Ai,
        ReturnsExternalText = true, Description = "Newest-first page of submissions, optionally for one form.")]
    Task<SubmissionPage> ListAsync(SubmissionQuery input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = FormsPlugin.SubmissionsReadPermission, Expose = OpExposure.Admin | OpExposure.Ai,
        ReturnsExternalText = true, Description = "One submission by id.")]
    Task<SubmissionSummary?> GetAsync(SubmissionRef input, CancellationToken ct);
}

/// <summary>Scoped to the providing instance: a consumer bound to one Forms instance sees only its submissions.</summary>
public sealed class FormSubmissions(IPluginContext context, FormsDbContext db) : IFormSubmissions
{
    public async Task<SubmissionPage> ListAsync(SubmissionQuery input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var page = Math.Max(input.Page, 1);
        var size = Math.Clamp(input.PageSize, 1, 100);
        var query = Mine();
        if (input.FormName is { Length: > 0 } form)
        {
            query = query.Where(s => s.FormName == form);
        }
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(s => s.SubmittedAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new SubmissionPage(rows.Select(ToSummary).ToList(), page, size, total);
    }

    public async Task<SubmissionSummary?> GetAsync(SubmissionRef input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var row = await Mine().FirstOrDefaultAsync(s => s.Id == input.SubmissionId, ct);
        return row is null ? null : ToSummary(row);
    }

    private IQueryable<FormSubmission> Mine()
    {
        var instanceId = context.Instance?.InstanceId
            ?? throw new ContractValidationException("forms.submissions@1 is served by a Forms instance.");
        return db.Submissions.AsNoTracking().Where(s => s.TenantId == context.TenantId && s.PluginInstanceId == instanceId);
    }

    private static SubmissionSummary ToSummary(FormSubmission s) => new(
        s.Id, s.PluginInstanceId, s.FormName,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.DataJson) ?? [],
        s.VisitorId, s.SubmittedAt, s.HandledAt is not null);
}
