using System.Text.Json;
using Dcms.AdminApi.Media;
using Dcms.AdminApi.Sites.Git;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Tenancy.Usage;

/// <summary>
/// Measures everything one tenant stores and writes it to <c>tenancy.tenant_usage</c>: the rows
/// of every table in <see cref="RlsConfigurator.TenantTables"/>, media files, site builds, plugin
/// files and git repositories, each put in a category the consoles can show.
///
/// <para>Called by <see cref="TenantUsageWorker"/> for every tenant, and by the Recalculate
/// buttons for one. Report only: the storage cap still counts media files alone.</para>
/// </summary>
public sealed class TenantUsageMeasurer(
    TenancyDbContext db,
    SitesDbContext sites,
    MediaDbContext media,
    // Resolved inside each guarded call: a cluster without object storage configured still
    // measures its database.
    IServiceProvider services,
    IOptions<StorageOptions> storageOptions,
    ForgejoClient forgejo,
    IOptions<ForgejoOptions> forgejoOptions,
    PluginRegistry plugins,
    ILogger<TenantUsageMeasurer> logger)
{
    private IObjectStorage Storage => services.GetRequiredService<IObjectStorage>();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TenantUsage> MeasureAsync(Guid tenantId, CancellationToken ct)
    {
        // The database narrows every statement to this tenant, whatever scope the caller is in.
        using var rls = RlsScope.Tenant(tenantId);

        var items = new List<Item>();
        await MeasureTablesAsync(tenantId, items, ct);
        await MeasureMediaFilesAsync(tenantId, items, ct);
        await MeasurePluginFilesAsync(tenantId, items, ct);
        await MeasureSitesAsync(tenantId, items, ct);

        var categories = items
            .Where(i => i.Bytes > 0)
            .GroupBy(i => i.Category)
            .Select(g => new UsageCategory(
                g.Key,
                g.Sum(i => i.Bytes),
                g.GroupBy(i => (i.Key, i.Kind))
                    .Select(x => new UsageItem(x.Key.Key, Label(x.Key.Key, x.First().Label), x.Key.Kind,
                        x.Sum(i => i.Bytes), x.Any(i => i.Rows is not null) ? x.Sum(i => i.Rows ?? 0) : null))
                    .OrderByDescending(i => i.Bytes)
                    .ToList()))
            .OrderByDescending(c => c.Bytes)
            .ToList();

        var usage = await db.Usage.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.TenantId == tenantId, ct);
        if (usage is null)
        {
            usage = new TenantUsage { TenantId = tenantId };
            db.Usage.Add(usage);
        }
        usage.MeasuredAt = DateTimeOffset.UtcNow;
        usage.TotalBytes = categories.Sum(c => c.Bytes);
        usage.CategoriesJson = JsonSerializer.Serialize(categories, Json);
        await db.SaveChangesAsync(ct);
        return usage;
    }

    // ------------------------------------------------------------------ database rows

    private async Task MeasureTablesAsync(Guid tenantId, List<Item> items, CancellationToken ct)
    {
        var columns = await ColumnsAsync(ct);
        var factors = await IndexFactorsAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // A constant: SET takes no parameters. Every per-tenant scan below is on a TenantId index,
        // so a minute is generous; a table that blows it fails the measurement, not the database.
        const string limit = "SET LOCAL statement_timeout = 60000";
        await db.Database.ExecuteSqlRawAsync(limit, ct);

        foreach (var (schema, table) in RlsConfigurator.TenantTables)
        {
            var map = TenantUsageMap.Tables[(schema, table)];
            if (!columns.TryGetValue((schema, table), out var cols))
            {
                continue; // not migrated on this cluster
            }

            // ponytail: rows are measured as their stored column sizes plus a 28-byte tuple header,
            // then scaled by the table's index-to-heap ratio. Partial indexes that belong to one
            // tenant (Dynamic Apps field indexes) are therefore shared out across all of them; an
            // exact split needs per-index attribution.
            var size = string.Join(" + ", cols.Select(c => $"coalesce(pg_column_size(x.\"{c}\"), 0)"));
            // Every identifier comes from RlsConfigurator.TenantTables, TenantUsageMap or the
            // catalogue, never from input; the tenant is a parameter.
            var sql = $$"""
                SELECT {{map.Group ?? "''"}}::text AS "Key", count(*)::bigint AS "Rows",
                       coalesce(sum({{size}} + 28), 0)::bigint AS "Bytes"
                FROM "{{schema}}"."{{table}}" x {{map.Join}}
                WHERE x."TenantId" = {0}
                GROUP BY 1
                """;
            var rows = await db.Database.SqlQueryRaw<Measured>(sql, tenantId).ToListAsync(ct);
            var factor = factors.GetValueOrDefault((schema, table), 1d);

            foreach (var row in rows.Where(r => r.Rows > 0))
            {
                var key = map.Group is null ? map.Item : $"plugin:{row.Key}";
                items.Add(new Item(map.Category, key, null, "database", (long)(row.Bytes * factor), row.Rows));
            }
        }

        await tx.CommitAsync(ct);
    }

    private static Dictionary<(string, string), string[]>? _columns;

    /// <summary>Column names per tenant table. The schema only changes with a deploy, so once per process.</summary>
    private async Task<Dictionary<(string, string), string[]>> ColumnsAsync(CancellationToken ct)
    {
        if (_columns is not null) return _columns;
        var schemas = RlsConfigurator.TenantTables.Select(t => t.Schema).Distinct().ToArray();
        var rows = await db.Database.SqlQueryRaw<CatalogColumn>("""
            SELECT table_schema AS "Schema", table_name AS "Table", column_name AS "Column"
            FROM information_schema.columns WHERE table_schema = ANY({0})
            """, (object)schemas).ToListAsync(ct); // one array parameter, not params
        return _columns = rows
            .GroupBy(r => (r.Schema, r.Table))
            .ToDictionary(g => g.Key, g => g.Select(r => r.Column).ToArray());
    }

    /// <summary>
    /// Total size over heap size per table, partitions included: what each byte of row data costs
    /// once its indexes and TOAST are counted.
    /// </summary>
    private async Task<Dictionary<(string, string), double>> IndexFactorsAsync(CancellationToken ct)
    {
        var schemas = RlsConfigurator.TenantTables.Select(t => t.Schema).Distinct().ToArray();
        var rows = await db.Database.SqlQueryRaw<Factor>("""
            SELECT n.nspname AS "Schema", c.relname AS "Table",
                   coalesce((SELECT sum(pg_total_relation_size(p.relid)) FROM pg_partition_tree(c.oid) p)::float8
                            / nullif((SELECT sum(pg_table_size(p.relid)) FROM pg_partition_tree(c.oid) p), 0), 1) AS "Value"
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition AND n.nspname = ANY({0})
            """, (object)schemas).ToListAsync(ct); // one array parameter, not params
        return rows.ToDictionary(r => (r.Schema, r.Table), r => Math.Max(1, r.Value));
    }

    // ------------------------------------------------------------------ files

    private async Task MeasureMediaFilesAsync(Guid tenantId, List<Item> items, CancellationToken ct)
    {
        // The same numbers the storage cap is held to.
        var (originals, renditions) = await MediaFootprint.OfAsync(media, tenantId, ct);
        items.Add(new Item("media", "originals", null, "files", originals, null));
        items.Add(new Item("media", "renditions", null, "files", renditions, null));
    }

    private async Task MeasurePluginFilesAsync(Guid tenantId, List<Item> items, CancellationToken ct)
    {
        var pluginIds = await db.Database
            .SqlQueryRaw<string>("""SELECT DISTINCT "PluginId" AS "Value" FROM plugins.plugin_instances WHERE "TenantId" = {0}""", tenantId)
            .ToListAsync(ct);
        foreach (var pluginId in pluginIds)
        {
            // The PluginBlobs root (PlatformServiceContracts).
            var bytes = await TryAsync(() => Storage.SizeOfPrefixAsync(
                storageOptions.Value.MediaBucket, $"tenants/{tenantId}/plugins/{pluginId}/", ct), "plugin files", ct);
            if (bytes is > 0)
            {
                items.Add(new Item("plugins", $"plugin:{pluginId}", null, "files", bytes.Value, null));
            }
        }
    }

    private async Task MeasureSitesAsync(Guid tenantId, List<Item> items, CancellationToken ct)
    {
        var list = await sites.Sites.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.GitRepoFullName,
                Live = s.Builds.Where(b => b.Id == s.ActiveBuildId).Select(b => b.ArtifactPrefix).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var bucket = storageOptions.Value.SitesBucket;
        foreach (var site in list)
        {
            // Every build ever published stays under the site's prefix until the site is deleted.
            var all = await TryAsync(() => Storage.SizeOfPrefixAsync(bucket, $"{tenantId}/{site.Id}/", ct), "site files", ct);
            var live = string.IsNullOrEmpty(site.Live)
                ? 0
                : await TryAsync(() => Storage.SizeOfPrefixAsync(bucket, site.Live.TrimEnd('/') + "/", ct), "site files", ct) ?? 0;
            if (all is not null)
            {
                items.Add(new Item("sites", $"site:{site.Id}:live", $"{site.Name} — published build", "files", live, null));
                items.Add(new Item("sites", $"site:{site.Id}:history", $"{site.Name} — earlier builds", "files", Math.Max(0, all.Value - live), null));
            }

            if (forgejoOptions.Value.Enabled && site.GitRepoFullName?.Split('/') is [var owner, var repo])
            {
                var info = await TryAsync(async () => (await forgejo.GetRepoAsync(owner, repo, ct))?.Size, "git repository", ct);
                if (info is > 0)
                {
                    items.Add(new Item("sites", $"site:{site.Id}:git", $"{site.Name} — git repository", "git", info.Value * 1024, null));
                }
            }
        }
    }

    /// <summary>A store that does not answer leaves its item out; it never fails the snapshot.</summary>
    private async Task<long?> TryAsync(Func<Task<long?>> measure, string what, CancellationToken ct)
    {
        try
        {
            return await measure();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Usage: could not measure {What}; left out of this snapshot.", what);
            return null;
        }
    }

    private Task<long?> TryAsync(Func<Task<long>> measure, string what, CancellationToken ct) =>
        TryAsync(async () => (long?)await measure(), what, ct);

    private string Label(string key, string? measured) =>
        measured
        ?? TenantUsageMap.Labels.GetValueOrDefault(key)
        ?? (key.StartsWith("plugin:", StringComparison.Ordinal)
            ? plugins.Find(key["plugin:".Length..])?.Name ?? key["plugin:".Length..]
            : key);

    private sealed record Item(string Category, string Key, string? Label, string Kind, long Bytes, long? Rows);

    // Classes with setters: raw SQL materializes by column name.
    private sealed class Measured
    {
        public string Key { get; set; } = "";
        public long Rows { get; set; }
        public long Bytes { get; set; }
    }

    private sealed class CatalogColumn
    {
        public string Schema { get; set; } = "";
        public string Table { get; set; } = "";
        public string Column { get; set; } = "";
    }

    private sealed class Factor
    {
        public string Schema { get; set; } = "";
        public string Table { get; set; } = "";
        public double Value { get; set; }
    }
}

public sealed record UsageCategory(string Key, long Bytes, IReadOnlyList<UsageItem> Items);

/// <param name="Kind"><c>database</c>, <c>files</c> or <c>git</c>.</param>
/// <param name="Rows">Database items only.</param>
public sealed record UsageItem(string Key, string Label, string Kind, long Bytes, long? Rows);
