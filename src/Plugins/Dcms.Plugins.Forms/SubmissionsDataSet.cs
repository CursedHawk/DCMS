using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.Forms;

/// <summary>
/// This instance's submissions on the plugin's admin page: filter by form and status, read one
/// with its fields labelled, mark handled, delete. The Forms inbox (<c>/forms</c>) is the same
/// data across every instance.
///
/// <para>Set-based statements here are audited by the data-set endpoints, not per statement: they
/// record every change as <c>plugin.data.*</c> through <c>IAuditRecorder</c>, naming the data
/// set, the action, the rows and the affected count.</para>
/// </summary>
public sealed class SubmissionsDataSet(IPluginContext context, FormsDbContext db) : IPluginDataSet
{
    private const string Handled = "handled";
    private const string Unhandled = "unhandled";

    private IReadOnlyList<FormDefinition> Forms =>
        context.Instance is { } instance ? FormsPlugin.ReadForms(instance.Config) : [];

    private Guid InstanceId => context.Instance?.InstanceId ?? Guid.Empty;

    public Task<DataSetSchema> DescribeAsync(CancellationToken ct) => Task.FromResult(new DataSetSchema(
        Columns:
        [
            new DataColumn("summary", "Submission", Primary: true),
            new DataColumn("form", "Form", DataColumnKinds.Badge),
            new DataColumn("status", "Status", DataColumnKinds.Badge),
            new DataColumn("submittedAt", "Received", DataColumnKinds.DateTime, Sortable: true),
        ],
        Filters:
        [
            new DataFilter("form", "Form", Forms.Select(f => new DataFilterOption(f.Name, f.Title ?? f.Name)).ToList()),
            new DataFilter("status", "Status", [new DataFilterOption(Unhandled, "To do"), new DataFilterOption(Handled, "Handled")]),
        ],
        Actions:
        [
            new DataAction(Handled, "Mark handled", OpRisk.Safe),
            new DataAction(Unhandled, "Mark as to do", OpRisk.Safe),
        ],
        CanDelete: true,
        DefaultSort: "submittedAt",
        DefaultDescending: true));

    public async Task<DataPage> ListAsync(DataQuery query, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var rows = db.Submissions.AsNoTracking().Where(s => s.PluginInstanceId == InstanceId);
        if (query.Filter("form") is { } form)
        {
            rows = rows.Where(s => s.FormName == form);
        }
        rows = query.Filter("status") switch
        {
            Handled => rows.Where(s => s.HandledAt != null),
            Unhandled => rows.Where(s => s.HandledAt == null),
            _ => rows,
        };
        rows = query.Descending ? rows.OrderByDescending(s => s.SubmittedAt) : rows.OrderBy(s => s.SubmittedAt);
        var total = await rows.LongCountAsync(ct);
        var page = await rows.Skip(query.Skip).Take(query.PageSize).ToListAsync(ct);
        var forms = Forms;
        return new DataPage(page.Select(s => ToRow(s, forms)).ToList(), total);
    }

    public async Task<DataRow?> GetAsync(string key, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        return Guid.TryParse(key, out var id)
            && await db.Submissions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.PluginInstanceId == InstanceId, ct) is { } row
                ? ToRow(row, Forms)
                : null;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        return Guid.TryParse(key, out var id)
            && await db.Submissions.Where(s => s.Id == id && s.PluginInstanceId == InstanceId).ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<DataActionResult> RunActionAsync(string action, IReadOnlyList<string> keys, JsonObject? input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var ids = keys.Select(k => Guid.TryParse(k, out var g) ? g : Guid.Empty).ToList();
        var mine = db.Submissions.Where(s => ids.Contains(s.Id) && s.PluginInstanceId == InstanceId);
        var now = DateTimeOffset.UtcNow;
        return action switch
        {
            Handled => new DataActionResult(await mine.Where(s => s.HandledAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.HandledAt, now), ct)),
            Unhandled => new DataActionResult(await mine.Where(s => s.HandledAt != null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.HandledAt, (DateTimeOffset?)null), ct)),
            _ => throw new NotSupportedException(),
        };
    }

    private static DataRow ToRow(FormSubmission s, IReadOnlyList<FormDefinition> forms)
    {
        var form = forms.FirstOrDefault(f => f.Name == s.FormName);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(s.DataJson) ? "{}" : s.DataJson);

        // Labelled by the form's own field labels, in the form's order; anything else after.
        var fields = new JsonObject();
        var data = doc.RootElement.ValueKind == JsonValueKind.Object
            ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)
            : [];
        foreach (var field in form?.Fields ?? [])
        {
            if (data.Remove(field.Name, out var value))
            {
                fields[field.Label ?? field.Name] = JsonNode.Parse(value.GetRawText());
            }
        }
        foreach (var (name, value) in data)
        {
            fields[name] = JsonNode.Parse(value.GetRawText());
        }

        var summary = string.Join(" · ", fields
            .Select(f => f.Value?.GetValueKind() == JsonValueKind.String ? f.Value.GetValue<string>() : f.Value?.ToJsonString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Take(3));
        return new DataRow(s.Id.ToString(), new JsonObject
        {
            ["summary"] = summary.Length > 160 ? summary[..160] + "…" : summary,
            ["form"] = form?.Title ?? s.FormName,
            ["status"] = s.HandledAt is null ? "to do" : "handled",
            ["submittedAt"] = s.SubmittedAt,
            ["handledAt"] = s.HandledAt,
            ["fields"] = fields,
            ["visitorId"] = s.VisitorId?.ToString(),
        }, form?.Title ?? s.FormName);
    }
}
