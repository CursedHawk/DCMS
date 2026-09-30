using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Visitors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.VisitorAuth;

/// <summary>
/// The site's visitor accounts on the plugin's admin page. Admins see and edit every attribute,
/// private ones included — the contract hides those from other plugins, not from the tenant.
/// Columns and the editor follow the attribute definitions in the instance config.
///
/// <para>Set-based statements here are audited by the data-set endpoints, not per statement: they
/// record every change as <c>plugin.data.*</c> through <c>IAuditRecorder</c>, naming the data
/// set, the action, the rows and the affected count.</para>
/// </summary>
public sealed class VisitorsDataSet(IPluginContext context, VisitorsDbContext db, ILogger<VisitorsDataSet> logger) : IPluginDataSet
{
    private const string SignOut = "sign-out";
    private const string Verify = "verify";

    private IReadOnlyList<AttributeDefinition> Definitions => VisitorProfiles.Definitions(context);

    public Task<DataSetSchema> DescribeAsync(CancellationToken ct)
    {
        var definitions = Definitions;
        var attributes = new JsonObject();
        foreach (var d in definitions)
        {
            attributes[d.Key] = d.Type switch
            {
                AttributeType.Number => new JsonObject { ["type"] = "number", ["title"] = d.Label },
                AttributeType.Boolean => new JsonObject { ["type"] = "boolean", ["title"] = d.Label },
                AttributeType.Date => new JsonObject { ["type"] = "string", ["format"] = "date", ["title"] = d.Label },
                AttributeType.Select => new JsonObject
                {
                    ["type"] = "string", ["title"] = d.Label,
                    ["enum"] = new JsonArray((d.Options ?? []).Select(o => (JsonNode?)o).ToArray()),
                },
                _ => new JsonObject { ["type"] = "string", ["maxLength"] = 1000, ["title"] = d.Label },
            };
        }

        var properties = new JsonObject
        {
            ["displayName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 256, ["title"] = "Display name" },
            ["emailVerified"] = new JsonObject { ["type"] = "boolean", ["title"] = "Email verified" },
        };
        if (definitions.Count > 0)
        {
            properties["attributes"] = new JsonObject { ["type"] = "object", ["title"] = "Profile", ["properties"] = attributes };
        }

        return Task.FromResult(new DataSetSchema(
            Columns:
            [
                new DataColumn("email", "Email", DataColumnKinds.Email, Sortable: true, Primary: true),
                new DataColumn("displayName", "Name", Sortable: true),
                new DataColumn("emailVerified", "Verified", DataColumnKinds.Boolean),
                .. definitions.Select(d => new DataColumn($"attributes.{d.Key}", d.Label, d.Type switch
                {
                    AttributeType.Number => DataColumnKinds.Number,
                    AttributeType.Boolean => DataColumnKinds.Boolean,
                    AttributeType.Select => DataColumnKinds.Badge,
                    _ => DataColumnKinds.Text,
                })),
                new DataColumn("createdAt", "Registered", DataColumnKinds.DateTime, Sortable: true),
            ],
            ItemSchema: new JsonObject { ["type"] = "object", ["properties"] = properties },
            Filters:
            [
                new DataFilter("verified", "Email", [new DataFilterOption("yes", "Verified"), new DataFilterOption("no", "Not verified")]),
            ],
            Actions:
            [
                new DataAction(SignOut, "Sign out everywhere", OpRisk.Safe,
                    Description: "Ends the visitor's sessions: they must sign in again once their current access token expires (minutes)."),
                new DataAction(Verify, "Mark email verified", OpRisk.Safe),
            ],
            Searchable: true,
            CanUpdate: true,
            CanDelete: true,
            DefaultSort: "createdAt",
            DefaultDescending: true));
    }

    public async Task<DataPage> ListAsync(DataQuery query, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var accounts = db.Accounts.AsNoTracking();
        if (query.Search is { } search)
        {
            var like = $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            accounts = accounts.Where(a => EF.Functions.ILike(a.Email, like) || EF.Functions.ILike(a.DisplayName ?? "", like));
        }
        accounts = query.Filter("verified") switch
        {
            "yes" => accounts.Where(a => a.EmailVerifiedAt != null),
            "no" => accounts.Where(a => a.EmailVerifiedAt == null),
            _ => accounts,
        };
        accounts = (query.Sort, query.Descending) switch
        {
            ("email", false) => accounts.OrderBy(a => a.Email),
            ("email", true) => accounts.OrderByDescending(a => a.Email),
            ("displayName", false) => accounts.OrderBy(a => a.DisplayName),
            ("displayName", true) => accounts.OrderByDescending(a => a.DisplayName),
            (_, false) => accounts.OrderBy(a => a.CreatedAt),
            _ => accounts.OrderByDescending(a => a.CreatedAt),
        };
        var total = await accounts.LongCountAsync(ct);
        var page = await accounts.Skip(query.Skip).Take(query.PageSize).ToListAsync(ct);
        var definitions = Definitions;
        return new DataPage(page.Select(a => ToRow(a, definitions)).ToList(), total);
    }

    public async Task<DataRow?> GetAsync(string key, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        return Guid.TryParse(key, out var id) && await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is { } account
            ? ToRow(account, Definitions)
            : null;
    }

    public async Task<DataRow?> UpdateAsync(string key, JsonObject values, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        if (!Guid.TryParse(key, out var id) || await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct) is not { } account)
        {
            return null;
        }
        var definitions = Definitions;

        if (values.TryGetPropertyValue("displayName", out var name))
        {
            account.DisplayName = name?.GetValue<string>() is { Length: > 0 } n ? n.Trim() : null;
        }
        if (values["emailVerified"]?.GetValue<bool>() is { } verified)
        {
            account.EmailVerifiedAt = verified ? account.EmailVerifiedAt ?? DateTimeOffset.UtcNow : null;
        }

        // The editor posts the whole profile: an attribute it leaves out was cleared.
        List<string> changed = [];
        if (values["attributes"] is JsonObject submitted)
        {
            var current = VisitorAttributes.Parse(account.AttributesJson);
            var changes = definitions.ToDictionary(
                d => d.Key,
                d => submitted[d.Key] is { } v ? JsonSerializer.SerializeToElement(v) : JsonSerializer.SerializeToElement<object?>(null));
            // Only real differences count, so an untouched save publishes no event.
            foreach (var (k, v) in changes.ToList())
            {
                var had = current.TryGetValue(k, out var old);
                if (v.ValueKind == JsonValueKind.Null ? !had : had && JsonElement.DeepEquals(old, v))
                {
                    changes.Remove(k);
                }
            }
            var next = VisitorAttributes.Apply(current, changes, definitions, _ => true, out changed);
            account.AttributesJson = VisitorAttributes.Serialize(next);
        }

        await db.SaveChangesAsync(ct);
        if (changed.Count > 0)
        {
            await VisitorProfiles.PublishUpdatedAsync(context, account.Id, changed, logger, ct);
        }
        return ToRow(account, definitions);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        if (!Guid.TryParse(key, out var id) || await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct) is not { } account)
        {
            return false;
        }
        await db.RefreshTokens.Where(t => t.VisitorId == id).ExecuteDeleteAsync(ct);
        db.Accounts.Remove(account);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<DataActionResult> RunActionAsync(string action, IReadOnlyList<string> keys, JsonObject? input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var ids = keys.Select(k => Guid.TryParse(k, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        var now = DateTimeOffset.UtcNow;
        return action switch
        {
            SignOut => new DataActionResult(await db.RefreshTokens
                .Where(t => ids.Contains(t.VisitorId) && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct)),
            Verify => new DataActionResult(await db.Accounts
                .Where(a => ids.Contains(a.Id) && a.EmailVerifiedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.EmailVerifiedAt, now), ct)),
            _ => throw new NotSupportedException(),
        };
    }

    private static DataRow ToRow(VisitorAccount account, IReadOnlyList<AttributeDefinition> definitions)
    {
        var stored = VisitorAttributes.Parse(account.AttributesJson);
        var attributes = new JsonObject();
        foreach (var d in definitions.Where(d => stored.ContainsKey(d.Key)))
        {
            attributes[d.Key] = JsonNode.Parse(stored[d.Key].GetRawText());
        }
        var values = new JsonObject
        {
            ["email"] = account.Email,
            ["displayName"] = account.DisplayName,
            ["emailVerified"] = account.EmailVerifiedAt is not null,
            ["createdAt"] = account.CreatedAt,
            ["attributes"] = attributes,
        };
        foreach (var (k, v) in attributes)
        {
            values[$"attributes.{k}"] = v?.DeepClone();
        }
        return new DataRow(account.Id.ToString(), values, account.DisplayName ?? account.Email);
    }
}
