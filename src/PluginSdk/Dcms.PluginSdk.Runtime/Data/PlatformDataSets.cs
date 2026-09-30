using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Platform;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.PluginSdk.Runtime.Data;

/// <summary>
/// The data sets a plugin has: the ones its manifest declares, plus one per platform store it
/// uses. A plugin that consumes <c>dcms.storage</c> or <c>dcms.blobs</c> — every installed plugin
/// with state, since it has no tables of its own — gets a data view without writing any code.
/// </summary>
public static class PluginDataSets
{
    public static readonly string StorageId = ContractIds.Of<IPluginStorage>().Split('@')[0];
    public static readonly string BlobsId = ContractIds.Of<IPluginBlobs>().Split('@')[0];

    public static IReadOnlyList<DataSetDeclaration> Of(PluginManifest manifest)
    {
        var sets = new List<DataSetDeclaration>(manifest.DataSets ?? []);
        var consumes = (manifest.Consumes ?? []).Select(c => c.ContractId).ToHashSet(StringComparer.Ordinal);
        if (consumes.Contains(ContractIds.Of<IPluginStorage>()))
        {
            sets.Add(new DataSetDeclaration(StorageId, "Stored data", typeof(StoredDocumentsDataSet),
                "Documents the plugin keeps in its private store (dcms.storage).", IconName: "Database"));
        }
        if (consumes.Contains(ContractIds.Of<IPluginBlobs>()))
        {
            sets.Add(new DataSetDeclaration(BlobsId, "Files", typeof(PluginFilesDataSet),
                "Files the plugin keeps in its private file area (dcms.blobs).", IconName: "FolderOpen"));
        }
        return sets;
    }
}

/// <summary>
/// A plugin's <c>dcms.storage</c> documents, for this instance and plugin-wide. Read straight
/// from <c>plugins.plugin_data</c> because the contract lists one collection at a time; scoped
/// the way <see cref="PluginStorage"/> scopes it — tenant, plugin and sandbox from the context,
/// never from input. Rows are addressed by their row id.
/// </summary>
internal sealed class StoredDocumentsDataSet(IPluginContext context, CmsDbContext db, ISandboxContext sandbox) : IPluginDataSet
{
    private const string Instance = "instance";
    private const string Plugin = "plugin";

    public async Task<DataSetSchema> DescribeAsync(CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var collections = await Mine().Select(d => d.Collection).Distinct().OrderBy(c => c).Take(200).ToListAsync(ct);
        return new DataSetSchema(
            Columns:
            [
                new DataColumn("collection", "Collection", Sortable: true),
                new DataColumn("key", "Key", Sortable: true, Primary: true),
                new DataColumn("scope", "Scope", DataColumnKinds.Badge),
                new DataColumn("version", "Version", DataColumnKinds.Number),
                new DataColumn("updatedAt", "Updated", DataColumnKinds.DateTime, Sortable: true),
            ],
            ItemSchema: new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("data"),
                ["properties"] = new JsonObject
                {
                    ["data"] = new JsonObject { ["type"] = "string", ["title"] = "Document (JSON)", ["format"] = "json" },
                },
            },
            Filters:
            [
                new DataFilter("collection", "Collection", collections.Select(c => new DataFilterOption(c, c)).ToList()),
                new DataFilter("scope", "Scope",
                    [new DataFilterOption(Instance, "This instance"), new DataFilterOption(Plugin, "Plugin-wide")]),
            ],
            Searchable: true,
            CanUpdate: true,
            CanDelete: true,
            DefaultSort: "updatedAt",
            DefaultDescending: true);
    }

    public async Task<DataPage> ListAsync(DataQuery query, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var rows = Mine().AsNoTracking();
        if (query.Filter("collection") is { } collection)
        {
            rows = rows.Where(d => d.Collection == collection);
        }
        rows = query.Filter("scope") switch
        {
            Instance => rows.Where(d => d.InstanceId != null),
            Plugin => rows.Where(d => d.InstanceId == null),
            _ => rows,
        };
        if (query.Search is { Length: > 0 } search)
        {
            rows = rows.Where(d => EF.Functions.ILike(d.Key, $"%{Like(search)}%"));
        }
        rows = (query.Sort, query.Descending) switch
        {
            ("collection", false) => rows.OrderBy(d => d.Collection).ThenBy(d => d.Key),
            ("collection", true) => rows.OrderByDescending(d => d.Collection).ThenBy(d => d.Key),
            ("key", false) => rows.OrderBy(d => d.Key),
            ("key", true) => rows.OrderByDescending(d => d.Key),
            (_, false) => rows.OrderBy(d => d.UpdatedAt),
            _ => rows.OrderByDescending(d => d.UpdatedAt),
        };
        var total = await rows.LongCountAsync(ct);
        var page = await rows.Skip(query.Skip).Take(query.PageSize).ToListAsync(ct);
        return new DataPage(page.Select(d => ToRow(d, withData: false)).ToList(), total);
    }

    public async Task<DataRow?> GetAsync(string key, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        return Guid.TryParse(key, out var id)
            && await Mine().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) is { } row
                ? ToRow(row, withData: true)
                : null;
    }

    public async Task<DataRow?> UpdateAsync(string key, JsonObject values, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        if (!Guid.TryParse(key, out var id) || await Mine().FirstOrDefaultAsync(d => d.Id == id, ct) is not { } row)
        {
            return null;
        }
        string json;
        try
        {
            json = JsonNode.Parse(values["data"]?.GetValue<string>() ?? "")?.ToJsonString()
                ?? throw new ContractValidationException("The document must be a JSON value, not empty.");
        }
        catch (JsonException e)
        {
            throw new ContractValidationException($"The document is not valid JSON: {e.Message}");
        }
        if (System.Text.Encoding.UTF8.GetByteCount(json) > PluginStorage.MaxDocumentBytes)
        {
            throw new ContractValidationException($"Document exceeds {PluginStorage.MaxDocumentBytes / 1024} KiB.");
        }
        row.DataJson = json;
        row.Version++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ContractConflictException("The document changed while you were editing it; reload and try again.");
        }
        return ToRow(row, withData: true);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        if (!Guid.TryParse(key, out var id) || await Mine().FirstOrDefaultAsync(d => d.Id == id, ct) is not { } row)
        {
            return false;
        }
        db.PluginData.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // This instance's documents and the plugin-wide ones; another instance's are not this page's.
    private IQueryable<PluginDatum> Mine()
    {
        var instanceId = context.Instance?.InstanceId;
        return db.PluginData.IgnoreQueryFilters().Where(d =>
            d.TenantId == context.TenantId && d.IsSandbox == sandbox.IsSandbox && d.PluginId == context.PluginId
            && (d.InstanceId == null || d.InstanceId == instanceId));
    }

    private static DataRow ToRow(PluginDatum d, bool withData)
    {
        var values = new JsonObject
        {
            ["collection"] = d.Collection,
            ["key"] = d.Key,
            ["scope"] = d.InstanceId is null ? Plugin : Instance,
            ["version"] = d.Version,
            ["updatedAt"] = d.UpdatedAt,
        };
        if (withData)
        {
            values["data"] = JsonNode.Parse(d.DataJson)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        return new DataRow(d.Id.ToString(), values, $"{d.Collection}/{d.Key}");
    }

    private static string Like(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

/// <summary>
/// A plugin's <c>dcms.blobs</c> files, through the contract itself so the key prefix stays the
/// platform's business. Paged in memory over the listing.
/// </summary>
// ponytail: lists every key per request; fine at the 10 MiB-per-file, exports-and-caches scale
// dcms.blobs is for. Needs a paged List on the contract if a plugin ever keeps thousands.
internal sealed class PluginFilesDataSet(IPluginContext context) : IPluginDataSet
{
    private IPluginBlobs Blobs => context.Contracts.Get<IPluginBlobs>();

    public Task<DataSetSchema> DescribeAsync(CancellationToken ct) => Task.FromResult(new DataSetSchema(
        Columns:
        [
            new DataColumn("key", "File", Sortable: true, Primary: true),
            new DataColumn("folder", "Folder"),
        ],
        Searchable: true,
        CanDelete: true,
        CanDownload: true,
        DefaultSort: "key"));

    public async Task<DataPage> ListAsync(DataQuery query, CancellationToken ct)
    {
        IEnumerable<string> keys = (await Blobs.ListAsync(new BlobPrefix(), ct)).Keys;
        if (query.Search is { Length: > 0 } search)
        {
            keys = keys.Where(k => k.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        var all = query.Descending ? keys.OrderDescending(StringComparer.Ordinal).ToList() : keys.Order(StringComparer.Ordinal).ToList();
        return new DataPage(all.Skip(query.Skip).Take(query.PageSize).Select(ToRow).ToList(), all.Count);
    }

    public async Task<DataRow?> GetAsync(string key, CancellationToken ct) =>
        await Blobs.GetAsync(new BlobKey(key), ct) is null ? null : ToRow(key);

    public async Task<bool> DeleteAsync(string key, CancellationToken ct) =>
        (await Blobs.DeleteAsync(new BlobKey(key), ct)).Found;

    public async Task<DataFile?> DownloadAsync(string key, CancellationToken ct) =>
        await Blobs.GetAsync(new BlobKey(key), ct) is { } blob
            ? new DataFile(new MemoryStream(blob.Content), blob.ContentType, key.Split('/')[^1])
            : null;

    private static DataRow ToRow(string key)
    {
        var slash = key.LastIndexOf('/');
        return new DataRow(key, new JsonObject
        {
            ["key"] = key,
            ["folder"] = slash > 0 ? key[..slash] : "",
        });
    }
}
