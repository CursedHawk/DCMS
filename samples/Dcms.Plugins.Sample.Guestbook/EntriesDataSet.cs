using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.Sample.Guestbook.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.PluginSdk.Abstractions.Platform;

namespace Dcms.Plugins.Sample.Guestbook;

/// <summary>
/// The guestbook's entries as a table on its instance page (Data tab). The console draws it from
/// this description alone — columns, a status filter, an editor for the editable values, row and
/// bulk actions — and the runtime enforces what it says: only declared capabilities are called,
/// values are cut to <see cref="DataSetSchema.ItemSchema"/> and validated, each change is audited.
/// The manifest gates reading on <c>read</c> and changing on <c>moderate</c>.
/// </summary>
public sealed class EntriesDataSet(IPluginContext context) : IPluginDataSet
{
    private const string Approve = "approve";
    private const string Reject = "reject";
    private const string Reply = "reply";
    private const string Erase = "erase";

    private readonly EntryStore _store = new(context);
    private Guid InstanceId => context.Instance?.InstanceId ?? throw new ContractValidationException("No guestbook.");

    public Task<DataSetSchema> DescribeAsync(CancellationToken ct) => Task.FromResult(new DataSetSchema(
        Columns:
        [
            new DataColumn("name", "Name", Primary: true),
            new DataColumn("message", "Message"),
            new DataColumn("status", "Status", DataColumnKinds.Badge),
            new DataColumn("signedAt", "Signed", DataColumnKinds.DateTime),
        ],
        // The editor: a moderator may fix a typo in the name or message, never the status (that
        // is what the actions are for, so each change of status is its own audited action).
        ItemSchema: new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("name", "message"),
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string", ["title"] = "Name", ["minLength"] = 1, ["maxLength"] = 80 },
                ["message"] = new JsonObject { ["type"] = "string", ["title"] = "Message", ["minLength"] = 1, ["maxLength"] = 2000 },
            },
        },
        Filters:
        [
            new DataFilter("status", "Status",
            [
                new DataFilterOption("pending", "Waiting"),
                new DataFilterOption("approved", "Shown"),
                new DataFilterOption("rejected", "Turned down"),
            ]),
        ],
        Actions:
        [
            new DataAction(Approve, "Approve", OpRisk.Safe, Description: "Shows the entries on the site."),
            new DataAction(Reject, "Reject", OpRisk.Safe, Description: "Hides the entries; they stay on record."),
            // An action with a form: the console asks for the reply before running it.
            new DataAction(Reply, "Reply", OpRisk.Safe, Bulk: false, Description: "A public answer shown under the entry.",
                InputSchema: new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("reply"),
                    ["properties"] = new JsonObject
                    {
                        ["reply"] = new JsonObject { ["type"] = "string", ["title"] = "Reply", ["maxLength"] = 500 },
                    },
                }),
            // Dangerous: the console confirms with a destructive button before it runs.
            new DataAction(Erase, "Erase permanently", OpRisk.Dangerous,
                Description: "Deletes the selected entries and every trace of them. This cannot be undone."),
        ],
        CanUpdate: true,
        CanDelete: true));

    public async Task<DataPage> ListAsync(DataQuery query, CancellationToken ct)
    {
        EntryStatus? status = query.Filter("status") switch
        {
            "pending" => EntryStatus.Pending,
            "approved" => EntryStatus.Approved,
            "rejected" => EntryStatus.Rejected,
            _ => null,
        };
        var (entries, total) = await _store.ListAsync(InstanceId, status, query.Page, query.PageSize, ct);
        return new DataPage(entries.Select(ToRow).ToList(), total);
    }

    public async Task<DataRow?> GetAsync(string key, CancellationToken ct) =>
        await _store.GetAsync(InstanceId, key, ct) is { } entry ? ToRow(entry) : null;

    public async Task<DataRow?> UpdateAsync(string key, JsonObject values, CancellationToken ct)
    {
        if (await _store.GetAsync(InstanceId, key, ct) is not { } entry)
        {
            return null;
        }
        var updated = entry with
        {
            Name = values["name"]!.GetValue<string>().Trim(),
            Message = values["message"]!.GetValue<string>().Trim(),
        };
        await _store.SaveAsync(InstanceId, updated, ct);
        return ToRow(updated);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct) => _store.DeleteAsync(InstanceId, key, ct);

    public async Task<DataActionResult> RunActionAsync(string action, IReadOnlyList<string> keys, JsonObject? input, CancellationToken ct)
    {
        var changed = 0;
        foreach (var key in keys)
        {
            if (await _store.GetAsync(InstanceId, key, ct) is not { } entry)
            {
                continue;
            }
            switch (action)
            {
                case Approve when entry.Status != EntryStatus.Approved:
                    await _store.SaveAsync(InstanceId, entry with { Status = EntryStatus.Approved }, ct);
                    await context.PublishAsync(new EntryApproved(InstanceId, entry.Id), ct);
                    changed++;
                    break;
                case Reject when entry.Status != EntryStatus.Rejected:
                    await _store.SaveAsync(InstanceId, entry with { Status = EntryStatus.Rejected }, ct);
                    changed++;
                    break;
                case Reply:
                    await _store.SaveAsync(InstanceId, entry with { Reply = input?["reply"]?.GetValue<string>().Trim() }, ct);
                    changed++;
                    break;
                case Erase:
                    changed += await _store.DeleteAsync(InstanceId, key, ct) ? 1 : 0;
                    break;
            }
        }
        return new DataActionResult(changed, action == Erase ? $"Erased {changed} for good." : null);
    }

    private static DataRow ToRow(GuestbookEntry e) => new(e.Id, new JsonObject
    {
        ["name"] = e.Name,
        ["message"] = e.Message,
        ["status"] = e.Status switch
        {
            EntryStatus.Pending => "waiting",
            EntryStatus.Approved => "shown",
            _ => "turned down",
        },
        ["signedAt"] = e.SignedAt,
        ["reply"] = e.Reply,
        ["visitorId"] = e.VisitorId?.ToString(),
    }, e.Name);
}
