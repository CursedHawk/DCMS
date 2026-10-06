using System.Diagnostics;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using DeleteBehavior = Dcms.Plugins.DynamicApps.Api.Model.DeleteBehavior;

namespace Dcms.Plugins.DynamicApps.Data;

/// <summary>
/// The data plane of one application: every read and write of its records, whichever plane
/// asks — the admin, the public site, the assistant, an automation. One path, so a record can
/// only ever be written the way the published model allows (ADR 0021).
///
/// <para>Writes run in a transaction that also claims unique keys (the database refuses a second
/// claim, so concurrent writers cannot both win), checks lookup targets and carries out what a
/// relationship says happens when its target is deleted. Each record carries a version; a write
/// that names a stale one is a conflict, never a lost update.</para>
/// </summary>
public sealed class RecordService(
    AppsDbContext db,
    IPluginContext context,
    RuntimeModelProvider models,
    IAuditRecorder audit,
    TimeProvider clock)
{
    public const int MaxBulk = 500;

    /// <summary>The most records one delete may take with it through cascading relationships.</summary>
    public const int MaxCascade = 500;

    private const int MaxCascadeDepth = 5;

    private Guid InstanceId => context.Instance?.InstanceId
        ?? throw new InvalidOperationException("Dynamic Apps records are per instance; this context has none.");

    private string? Actor => context.Actor.Id?.ToString() ?? context.Actor.Key;

    // ------------------------------------------------------------------ reads

    public async Task<RecordPage> QueryAsync(string tableName, RecordQuery query, RecordPlane plane, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, ct);
        using var activity = Span("dcms.dynamicapp.query", model, table);

        // Raw SQL by construction: QueryCompiler writes only its own identifiers and operators,
        // and every value — field keys included — is a parameter. Nothing the caller sent is in
        // these strings; the limit and offset are integers it validated.
        var compiled = new QueryCompiler(model, table, plane).Compile(query, context.TenantId, InstanceId);
        var pageSql = $"""
            SELECT r.* FROM apps.records r WHERE {compiled.Where}
            ORDER BY {compiled.OrderBy} LIMIT {compiled.Limit} OFFSET {compiled.Offset}
            """;
        var countSql = $"""SELECT COUNT(*)::int AS "Value" FROM apps.records r WHERE {compiled.Where}""";
        var rows = await db.Records
            .FromSqlRaw(pageSql, compiled.Parameters())
            .IgnoreQueryFilters() // the tenant is in the compiled WHERE, under this method's RlsScope
            .AsNoTracking()
            .ToListAsync(ct);
        var total = await db.Database.SqlQueryRaw<int>(countSql, compiled.Parameters()).SingleAsync(ct);

        var items = rows.Select(r => RecordCodec.Read(table, r, plane, query.Select)).ToList();
        await ExpandAsync(model, table, rows, items, query.Expand, plane, ct);
        activity?.SetTag("dcms.dynamicapp.rows", rows.Count);
        return new RecordPage(items, total, query.Page, query.PageSize);
    }

    public async Task<JsonObject?> GetAsync(string tableName, Guid id, IReadOnlyList<string> expand, RecordPlane plane, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, ct);
        foreach (var name in expand)
        {
            if (!table.ByName.TryGetValue(name, out var m) || !m.IsLookup || !RecordCodec.Visible(m, plane))
            {
                throw new ContractValidationException($"'{name}' is not a lookup of {table.ApiName}; only lookups expand.");
            }
        }
        var record = await Records(table).AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null)
        {
            return null;
        }
        var item = RecordCodec.Read(table, record, plane);
        await ExpandAsync(model, table, [record], [item], expand, plane, ct);
        return item;
    }

    /// <summary>The records related to one through a navigation: its lookup's target, the records pointing at it, or its many-to-many links.</summary>
    public async Task<RecordPage?> RelatedAsync(string tableName, Guid id, string navigation, int page, int pageSize, RecordPlane plane, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, ct);
        var nav = Navigation(table, navigation);
        if (!model.ById.TryGetValue(nav.OtherTableId, out var other))
        {
            throw new ContractValidationException($"'{navigation}' leads to a table that is not enabled.");
        }
        pageSize = Math.Clamp(pageSize, 1, QueryCompiler.MaxPageSize);
        page = Math.Max(page, 1);
        var record = await Records(table).AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null)
        {
            return null;
        }

        IQueryable<AppRecord> related;
        switch (nav.Kind)
        {
            case NavigationKind.Lookup:
                var target = (JsonNode.Parse(record.Data)?[nav.Relationship.Id.ToString()] as JsonValue)?.GetValue<string>();
                var targetId = Guid.TryParse(target, out var t) ? t : Guid.Empty;
                related = Records(other).Where(r => r.Id == targetId);
                break;
            case NavigationKind.Inverse:
                related = Records(other).Where(r => EF.Functions.JsonContains(r.Data, Pointer(nav.Relationship.Id, id)));
                break;
            default:
                var links = db.RelationLinks.Where(l => l.InstanceId == InstanceId && l.RelationshipId == nav.Relationship.Id);
                var ids = nav.FromSource
                    ? links.Where(l => l.SourceId == id).Select(l => l.TargetId)
                    : links.Where(l => l.TargetId == id).Select(l => l.SourceId);
                related = Records(other).Where(r => ids.Contains(r.Id));
                break;
        }
        var total = await related.CountAsync(ct);
        var rows = await related.AsNoTracking().OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new RecordPage(rows.Select(r => RecordCodec.Read(other, r, plane)).ToList(), total, page, pageSize);
    }

    // ------------------------------------------------------------------ writes

    public async Task<JsonObject> CreateAsync(string tableName, JsonObject values, RecordPlane plane, CancellationToken ct, Guid? ownerVisitorId = null)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, ct);
        using var activity = Span("dcms.dynamicapp.mutation", model, table, "create");
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var write = RecordCodec.Write(table, values, null, plane);
        await CheckLookupsAsync(model, table, write.Data, write.Changed, ct);
        var now = clock.GetUtcNow();
        var record = new AppRecord
        {
            Id = Guid.NewGuid(),
            InstanceId = InstanceId,
            TableId = table.Id,
            Data = write.Data.ToJsonString(),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = Actor,
            UpdatedBy = Actor,
            OwnerVisitorId = ownerVisitorId,
        };
        db.Records.Add(record);
        await ClaimKeysAsync(table, record.Id, write.Data, ct);

        Declare("record.created", table, record.Id);
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
        return RecordCodec.Read(table, record, plane);
    }

    /// <summary>Changes the fields <paramref name="values"/> names. Null when the record does not exist.</summary>
    /// <exception cref="ContractConflictException">A version was named and is no longer current, or a unique value is taken.</exception>
    public async Task<JsonObject?> UpdateAsync(string tableName, Guid id, JsonObject values, RecordPlane plane, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, ct);
        using var activity = Span("dcms.dynamicapp.mutation", model, table, "update");
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var record = await Records(table).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null)
        {
            return null;
        }
        await UpdateCoreAsync(model, table, record, values, plane, ct);
        Declare("record.updated", table, record.Id);
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
        return RecordCodec.Read(table, record, plane);
    }

    /// <summary>False when the record does not exist.</summary>
    /// <exception cref="ContractConflictException">The version is stale, or a restricting relationship still points at it.</exception>
    public async Task<bool> DeleteAsync(string tableName, Guid id, int? expectedVersion, RecordPlane plane, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, ct);
        using var activity = Span("dcms.dynamicapp.mutation", model, table, "delete");
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var record = await Records(table).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null)
        {
            return false;
        }
        if (expectedVersion is { } v && v != record.Version)
        {
            throw Stale(record);
        }
        var removed = new HashSet<Guid>();
        await DeleteCoreAsync(model, table, record, removed, 0, ct);
        Declare("record.deleted", table, record.Id).With("cascaded", removed.Count - 1);
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<BulkResult> BulkUpdateAsync(string tableName, BulkUpdateRequest request, RecordPlane plane, CancellationToken ct)
    {
        Bounded(request.Ids);
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, ct);
        using var activity = Span("dcms.dynamicapp.mutation", model, table, "bulk-update");
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var ids = request.Ids.Distinct().ToList();
        var records = await Records(table).Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        foreach (var record in records)
        {
            await UpdateCoreAsync(model, table, record, request.Values, plane, ct);
        }
        Declare("record.bulk_updated", table, null).With("count", records.Count);
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
        return new BulkResult(records.Count);
    }

    public async Task<BulkResult> BulkDeleteAsync(string tableName, BulkDeleteRequest request, RecordPlane plane, CancellationToken ct)
    {
        Bounded(request.Ids);
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, ct);
        using var activity = Span("dcms.dynamicapp.mutation", model, table, "bulk-delete");
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var ids = request.Ids.Distinct().ToList();
        var records = await Records(table).Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        var removed = new HashSet<Guid>();
        foreach (var record in records.Where(r => !removed.Contains(r.Id)))
        {
            await DeleteCoreAsync(model, table, record, removed, 0, ct);
        }
        Declare("record.bulk_deleted", table, null).With("count", records.Count).With("cascaded", removed.Count - records.Count);
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
        return new BulkResult(records.Count);
    }

    /// <summary>Links two records through a many-to-many relationship. Linking twice is not an error.</summary>
    /// <returns>False when either record does not exist.</returns>
    public async Task<bool> LinkAsync(string tableName, Guid id, string navigation, Guid targetId, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, ct);
        var nav = ManyToMany(table, navigation);
        var other = model.ById.GetValueOrDefault(nav.OtherTableId)
                    ?? throw new ContractValidationException($"'{navigation}' leads to a table that is not enabled.");
        if (!await Records(table).AnyAsync(r => r.Id == id, ct) || !await Records(other).AnyAsync(r => r.Id == targetId, ct))
        {
            return false;
        }
        var (source, target) = nav.FromSource ? (id, targetId) : (targetId, id);
        var exists = await db.RelationLinks.AnyAsync(l =>
            l.InstanceId == InstanceId && l.RelationshipId == nav.Relationship.Id && l.SourceId == source && l.TargetId == target, ct);
        if (!exists)
        {
            db.RelationLinks.Add(new AppRelationLink
            {
                Id = Guid.NewGuid(),
                InstanceId = InstanceId,
                RelationshipId = nav.Relationship.Id,
                SourceId = source,
                TargetId = target,
                CreatedAt = clock.GetUtcNow(),
            });
            Declare("relation.linked", table, id).With("relationship", nav.Relationship.ApiName).With("target", targetId);
            await SaveAsync(ct);
        }
        return true;
    }

    /// <returns>False when there was no such link.</returns>
    public async Task<bool> UnlinkAsync(string tableName, Guid id, string navigation, Guid targetId, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (_, table) = await TableAsync(tableName, ct);
        var nav = ManyToMany(table, navigation);
        var (source, target) = nav.FromSource ? (id, targetId) : (targetId, id);
        Declare("relation.unlinked", table, id).With("relationship", nav.Relationship.ApiName).With("target", targetId);
        // Set-based, and recorded: the declared entry above names the link that goes.
        var removed = await db.RelationLinks
            .Where(l => l.InstanceId == InstanceId && l.RelationshipId == nav.Relationship.Id && l.SourceId == source && l.TargetId == target)
            .ExecuteDeleteAsync(ct);
        return removed > 0;
    }

    // ------------------------------------------------------------------ internals

    private async Task UpdateCoreAsync(RuntimeModel model, RuntimeTable table, AppRecord record, JsonObject values, RecordPlane plane, CancellationToken ct)
    {
        var existing = JsonNode.Parse(record.Data) as JsonObject ?? [];
        var write = RecordCodec.Write(table, values, existing, plane);
        if (write.ExpectedVersion is { } v && v != record.Version)
        {
            throw Stale(record);
        }
        if (write.Changed.Count == 0)
        {
            return;
        }
        await CheckLookupsAsync(model, table, write.Data, write.Changed, ct);
        record.Data = write.Data.ToJsonString();
        record.Version++;
        record.UpdatedAt = clock.GetUtcNow();
        record.UpdatedBy = Actor;
        await ClaimKeysAsync(table, record.Id, write.Data, ct);
    }

    /// <summary>
    /// Deletes a record and does what each relationship pointing at it says: refuse
    /// (restrict), clear the pointer (set null), or delete the pointing records too (cascade),
    /// up to <see cref="MaxCascade"/> records and <see cref="MaxCascadeDepth"/> hops.
    /// </summary>
    private async Task DeleteCoreAsync(RuntimeModel model, RuntimeTable table, AppRecord record, HashSet<Guid> removed, int depth, CancellationToken ct)
    {
        if (!removed.Add(record.Id))
        {
            return;
        }
        if (removed.Count > MaxCascade || depth > MaxCascadeDepth)
        {
            throw new ContractConflictException(
                $"Deleting this would remove more than {MaxCascade} records through cascading relationships; delete the related records first.");
        }

        foreach (var relationship in model.Config.Relationships.Where(r => r.TargetTableId == table.Id && r.Kind != RelationshipKind.ManyToMany))
        {
            if (!model.ById.TryGetValue(relationship.SourceTableId, out var source))
            {
                continue; // a disabled table's records keep their pointer; they are not served
            }
            var pointing = await Records(source)
                .Where(r => EF.Functions.JsonContains(r.Data, Pointer(relationship.Id, record.Id)))
                .Take(MaxCascade + 1)
                .ToListAsync(ct);
            pointing.RemoveAll(r => removed.Contains(r.Id));
            if (pointing.Count == 0)
            {
                continue;
            }
            switch (relationship.OnDelete)
            {
                case DeleteBehavior.Restrict:
                    throw new ContractConflictException(
                        $"{pointing.Count} {source.ApiName} record(s) still point at this one through '{relationship.ApiName}'.");
                case DeleteBehavior.SetNull:
                    foreach (var r in pointing)
                    {
                        var data = JsonNode.Parse(r.Data)!.AsObject();
                        data.Remove(relationship.Id.ToString());
                        r.Data = data.ToJsonString();
                        r.Version++;
                        r.UpdatedAt = clock.GetUtcNow();
                        r.UpdatedBy = Actor;
                    }
                    break;
                default:
                    foreach (var r in pointing)
                    {
                        await DeleteCoreAsync(model, source, r, removed, depth + 1, ct);
                    }
                    break;
            }
        }

        db.Records.Remove(record);
        // Set-based, under the record.deleted entry the caller declares.
        await db.UniqueKeys.Where(k => k.InstanceId == InstanceId && k.RecordId == record.Id).ExecuteDeleteAsync(ct);
        await db.RelationLinks.Where(l => l.InstanceId == InstanceId && (l.SourceId == record.Id || l.TargetId == record.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>Every lookup the write changed points at an existing record of its target table.</summary>
    private async Task CheckLookupsAsync(RuntimeModel model, RuntimeTable table, JsonObject data, IReadOnlyList<string> changed, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in table.Members.Where(m => m.IsLookup && changed.Contains(m.ApiName)))
        {
            if (data[member.Key] is not JsonValue value || !Guid.TryParse(value.GetValue<string>(), out var target))
            {
                continue;
            }
            if (!model.ById.TryGetValue(member.Lookup!.TargetTableId, out var targetTable)
                || !await Records(targetTable).AnyAsync(r => r.Id == target, ct))
            {
                errors[member.ApiName] = $"there is no {(targetTable?.ApiName ?? "target")} record {target}.";
            }
        }
        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    /// <summary>Brings the record's claimed unique keys in line with its data; a key another record holds is a conflict.</summary>
    private async Task ClaimKeysAsync(RuntimeTable table, Guid recordId, JsonObject data, CancellationToken ct)
    {
        var wanted = RecordCodec.UniqueKeys(table, data).ToHashSet();
        var held = await db.UniqueKeys.Where(k => k.InstanceId == InstanceId && k.RecordId == recordId).ToListAsync(ct);
        foreach (var key in held.Where(k => !wanted.Contains((k.ConstraintId, k.Key))))
        {
            db.UniqueKeys.Remove(key);
        }
        foreach (var (constraintId, key) in wanted.Where(w => !held.Any(h => h.ConstraintId == w.ConstraintId && h.Key == w.Key)))
        {
            if (await db.UniqueKeys.AnyAsync(k => k.InstanceId == InstanceId && k.ConstraintId == constraintId && k.Key == key && k.RecordId != recordId, ct))
            {
                throw new ContractConflictException($"Another {table.ApiName} record already has this {Describe(table, constraintId)}.");
            }
            db.UniqueKeys.Add(new AppUniqueKey { Id = Guid.NewGuid(), InstanceId = InstanceId, ConstraintId = constraintId, RecordId = recordId, Key = key });
        }
    }

    private static string Describe(RuntimeTable table, Guid constraintId) =>
        table.Uniques.FirstOrDefault(u => u.Id == constraintId) is { } u
            ? string.Join(" + ", u.Members.Select(m => m.ApiName))
            : "value";

    /// <summary>Saves, turning the database's refusals into the caller's conflicts.</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ContractConflictException("The record changed while you were editing it. Read it again and retry.");
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent writer claimed the same unique value between our check and our insert.
            throw new ContractConflictException("Another record took this unique value at the same moment. Retry.");
        }
    }

    private async Task ExpandAsync(RuntimeModel model, RuntimeTable table, IReadOnlyList<AppRecord> rows, IReadOnlyList<JsonObject> items,
        IReadOnlyList<string> expand, RecordPlane plane, CancellationToken ct)
    {
        foreach (var name in expand.Distinct())
        {
            var member = table.ByName[name];
            if (!model.ById.TryGetValue(member.Lookup!.TargetTableId, out var target))
            {
                continue;
            }
            var ids = rows.Select(r => (JsonNode.Parse(r.Data)?[member.Key] as JsonValue)?.GetValue<string>())
                .Where(s => s is not null).Select(s => Guid.Parse(s!)).Distinct().ToList();
            var targets = await Records(target).AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            foreach (var item in items)
            {
                if (item[name] is JsonValue v && Guid.TryParse(v.GetValue<string>(), out var id) && targets.TryGetValue(id, out var hit))
                {
                    item[name] = RecordCodec.Read(target, hit, plane);
                }
            }
        }
    }

    private async Task<(RuntimeModel Model, RuntimeTable Table)> TableAsync(string tableName, CancellationToken ct)
    {
        var model = await models.GetAsync(ct)
                    ?? throw new ContractValidationException("This application has no published tables yet.");
        var table = model.Table(tableName) ?? throw new ContractValidationException($"There is no table '{tableName}'.");
        return (model, table);
    }

    private IQueryable<AppRecord> Records(RuntimeTable table)
    {
        var instanceId = InstanceId;
        return db.Records.Where(r => r.InstanceId == instanceId && r.TableId == table.Id);
    }

    private static RuntimeNavigation Navigation(RuntimeTable table, string name) =>
        table.Navigations.GetValueOrDefault(name) ?? throw new ContractValidationException($"{table.ApiName} has no relationship '{name}'.");

    private static RuntimeNavigation ManyToMany(RuntimeTable table, string name) =>
        Navigation(table, name) is { Kind: NavigationKind.ManyToMany } nav
            ? nav
            : throw new ContractValidationException($"'{name}' is a lookup; set it on the record instead of linking.");

    private static string Pointer(Guid relationshipId, Guid target) =>
        new JsonObject { [relationshipId.ToString()] = target.ToString() }.ToJsonString();

    private static void Bounded(IReadOnlyList<Guid> ids)
    {
        if (ids.Count is 0 or > MaxBulk)
        {
            throw new ContractValidationException($"A bulk operation takes 1 to {MaxBulk} ids.");
        }
    }

    private static ContractConflictException Stale(AppRecord record) =>
        new($"The record changed since you read it; it is at version {record.Version}. Read it again and retry.");

    private AuditEntry Declare(string action, RuntimeTable table, Guid? id) =>
        (audit.Declared ?? audit.Record($"plugin.{DynamicAppsPlugin.PluginId}.{action}"))
            .For("app_record", id, $"{table.ApiName}/{id}")
            .With("instance", InstanceId)
            .With("table", table.ApiName);

    private Activity? Span(string name, RuntimeModel model, RuntimeTable table, string? operation = null)
    {
        var activity = DcmsActivitySource.Start(name);
        activity?.SetTag("dcms.tenant", context.TenantId);
        activity?.SetTag("dcms.dynamicapp.instance", InstanceId);
        activity?.SetTag("dcms.dynamicapp.table", table.ApiName);
        activity?.SetTag("dcms.dynamicapp.revision", model.Revision.Number);
        if (operation is not null)
        {
            activity?.SetTag("dcms.dynamicapp.operation", operation);
        }
        return activity;
    }
}
