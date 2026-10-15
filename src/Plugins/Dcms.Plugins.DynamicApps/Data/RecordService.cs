using System.Diagnostics;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Kernel.Views;
using Dcms.Shared.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
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
    AppEventLog events,
    IAuditRecorder audit,
    TimeProvider clock,
    IHttpContextAccessor http)
{
    public const int MaxBulk = 500;

    /// <summary>Where a page's total stops counting (RecordPage.TotalCapped).</summary>
    public const int MaxCount = 5_000;

    /// <summary>The most records one import creates.</summary>
    public const int MaxImport = 1000;

    private const int MaxImportErrors = 100;

    /// <summary>The most records one delete may take with it through cascading relationships.</summary>
    public const int MaxCascade = 500;

    private const int MaxCascadeDepth = 5;

    /// <summary>
    /// The signed-in site visitor, set by the public routes before they call: whose "own"
    /// records the public plane sees and may change, and who owns what they create.
    /// </summary>
    public Guid? Visitor { get; set; }

    /// <summary>The signed-in user as the tables' row rules see them; set with <see cref="Visitor"/>.</summary>
    public RowSubject? Subject { get; set; }

    /// <summary>
    /// What the signed-in enterprise user may do here beyond the public access (ADR 0022): their
    /// site permissions on this app, set by the public routes from <c>users.access@1</c>, without
    /// the <c>dynamic-apps:{slug}:</c> prefix — <c>table:deals:read</c>. Holding one lifts the
    /// public access for that action and table and reaches every record — except on a table with
    /// row rules, where it reaches the rows the rules let them read, unless held through
    /// <see cref="Bypass"/>.
    /// </summary>
    public IReadOnlySet<string> Members { get; set; } = new HashSet<string>();

    /// <summary>The <see cref="Members"/> permissions held through a role that bypasses row-level access.</summary>
    public IReadOnlySet<string> Bypass { get; set; } = new HashSet<string>();

    // Numbers each scoped query's parameters apart, should two ever be composed into one command.
    private int _scopes;

    private static string Permission(RuntimeTable table, PublicOp op) => $"table:{table.ApiName}:{op.ToString().ToLowerInvariant()}";

    private bool Member(RuntimeTable table, PublicOp op) => Members.Contains(Permission(table, op));

    /// <summary>Reaches every record for this action: through a bypassing role, or a role on a table with no row rules.</summary>
    private bool Unscoped(RuntimeTable table, PublicOp op) =>
        Member(table, op) && (table.Def.Public.Rules.Count == 0 || Bypass.Contains(Permission(table, op)));

    private Guid InstanceId => context.Instance?.InstanceId
        ?? throw new InvalidOperationException("Dynamic Apps records are per instance; this context has none.");

    private string? Actor => ActingAs ?? context.Actor.Id?.ToString() ?? context.Actor.Key;

    /// <summary>
    /// Who the records are attributed to when the caller is not the person: a bulk job run by
    /// jobs-worker acts as the system, but its records were created by whoever queued it.
    /// </summary>
    public string? ActingAs { get; set; }

    /// <summary>Told (done, total, stage) as a long operation goes; a background job's bar.</summary>
    public delegate ValueTask Progress(long done, long total, string stageKey);

    // ------------------------------------------------------------------ reads

    public async Task<RecordPage> QueryAsync(string tableName, RecordQuery query, RecordPlane plane, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, plane, PublicOp.Read, ct);
        using var activity = Span("dcms.dynamicapp.query", model, table);
        query = query with { Filter = WithViewer(query.Filter, plane) };

        // Raw SQL by construction: QueryCompiler writes only its own identifiers and operators,
        // and every value — field keys included — is a parameter. Nothing the caller sent is in
        // these strings; the limit and offset are integers it validated.
        var compiled = new QueryCompiler(model, table, plane).Compile(query, context.TenantId, InstanceId, Scope(model, table, plane, PublicOp.Read));
        var pageSql = $"""
            SELECT r.* FROM apps.records r WHERE {compiled.Where}
            ORDER BY {compiled.OrderBy} LIMIT {compiled.Limit} OFFSET {compiled.Offset}
            """;
        // Counted up to MaxCount + 1 rows and no further: an exact count read every matching row.
        var countSql = $"""SELECT COUNT(*)::int AS "Value" FROM (SELECT 1 FROM apps.records r WHERE {compiled.Where} LIMIT {MaxCount + 1}) s""";
        var rows = await db.Records
            .FromSqlRaw(pageSql, compiled.Parameters())
            .IgnoreQueryFilters() // the tenant is in the compiled WHERE, under this method's RlsScope
            .AsNoTracking()
            .ToListAsync(ct);
        var counted = await db.Database.SqlQueryRaw<int>(countSql, compiled.Parameters()).SingleAsync(ct);

        var items = rows.Select(r => RecordCodec.Read(table, r, plane, query.Select)).ToList();
        await ExpandAsync(model, table, rows, items, query.Expand, plane, ct);
        activity?.SetTag("dcms.dynamicapp.rows", rows.Count);
        return new RecordPage(items, Math.Min(counted, MaxCount), query.Page, query.PageSize, counted > MaxCount);
    }

    public async Task<JsonObject?> GetAsync(string tableName, Guid id, IReadOnlyList<string> expand, RecordPlane plane, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, plane, PublicOp.Read, ct);
        foreach (var name in expand)
        {
            if (!table.ByName.TryGetValue(name, out var m) || !m.IsLookup || !RecordCodec.Visible(m, plane))
            {
                throw new ContractValidationException($"'{name}' is not a lookup of {table.ApiName}; only lookups expand.");
            }
        }
        var record = await Records(model, table, plane).AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
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
        var (model, table) = await TableAsync(tableName, plane, PublicOp.Read, ct);
        var nav = Navigation(table, navigation);
        if (!model.ById.TryGetValue(nav.OtherTableId, out var other) || !Readable(other, plane))
        {
            throw plane == RecordPlane.Public
                ? new PublicAccessException(404, $"{table.ApiName} has no relationship '{navigation}'.")
                : new ContractValidationException($"'{navigation}' leads to a table that is not enabled.");
        }
        pageSize = Math.Clamp(pageSize, 1, QueryCompiler.MaxPageSize);
        page = Math.Max(page, 1);
        var record = await Records(model, table, plane).AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
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
                related = Records(model, other, plane).Where(r => r.Id == targetId);
                break;
            case NavigationKind.Inverse:
                related = Records(model, other, plane).Where(r => EF.Functions.JsonContains(r.Data, Pointer(nav.Relationship.Id, id)));
                break;
            default:
                var links = db.RelationLinks.Where(l => l.InstanceId == InstanceId && l.RelationshipId == nav.Relationship.Id);
                var ids = nav.FromSource
                    ? links.Where(l => l.SourceId == id).Select(l => l.TargetId)
                    : links.Where(l => l.TargetId == id).Select(l => l.SourceId);
                related = Records(model, other, plane).Where(r => ids.Contains(r.Id));
                break;
        }
        var counted = await related.Take(MaxCount + 1).CountAsync(ct);
        var rows = await related.AsNoTracking().OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new RecordPage(rows.Select(r => RecordCodec.Read(other, r, plane)).ToList(), Math.Min(counted, MaxCount), page, pageSize, counted > MaxCount);
    }

    // ------------------------------------------------------------------ writes

    public async Task<JsonObject> CreateAsync(string tableName, JsonObject values, RecordPlane plane, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, plane, PublicOp.Create, ct);
        using var activity = Span("dcms.dynamicapp.mutation", model, table, "create");
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var write = RecordCodec.Write(table, values, null, plane);
        await CheckLookupsAsync(model, table, write.Data, write.Changed, plane, ct);
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
            // A record created from the site belongs to whoever is signed in there.
            OwnerVisitorId = plane == RecordPlane.Public ? Visitor : null,
        };
        db.Records.Add(record);
        await ClaimKeysAsync(table, record.Id, write.Data, ct);
        events.Add(db, model.Revision.Number, AppEvent.RowCreated, Entity(table, record.Id),
            new JsonObject { ["record"] = RecordCodec.Read(table, record, RecordPlane.System) });

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
        var (model, table) = await TableAsync(tableName, plane, PublicOp.Update, ct);
        using var activity = Span("dcms.dynamicapp.mutation", model, table, "update");
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var record = await Records(model, table, plane, PublicOp.Update).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null)
        {
            return null;
        }
        await UpdateCoreAsync(model, table, record, values, plane, ct);
        Declare("record.updated", table, record.Id);
        await SaveAsync(ct);
        await CheckReachAsync(model, table, record.Id, plane, ct);
        await tx.CommitAsync(ct);
        return RecordCodec.Read(table, record, plane);
    }

    /// <summary>False when the record does not exist.</summary>
    /// <exception cref="ContractConflictException">The version is stale, or a restricting relationship still points at it.</exception>
    public async Task<bool> DeleteAsync(string tableName, Guid id, int? expectedVersion, RecordPlane plane, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, plane, PublicOp.Delete, ct);
        using var activity = Span("dcms.dynamicapp.mutation", model, table, "delete");
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var record = await Records(model, table, plane, PublicOp.Delete).FirstOrDefaultAsync(r => r.Id == id, ct);
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

    public async Task<BulkResult> BulkUpdateAsync(
        string tableName, BulkUpdateRequest request, RecordPlane plane, CancellationToken ct,
        int maxIds = MaxBulk, Progress? progress = null)
    {
        Bounded(request.Ids, maxIds);
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, RecordPlane.Admin, PublicOp.Read, ct);
        using var activity = Span("dcms.dynamicapp.mutation", model, table, "bulk-update");
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var ids = request.Ids.Distinct().ToList();
        var records = await Records(table).Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        var done = 0;
        foreach (var record in records)
        {
            events.NextAction();
            await UpdateCoreAsync(model, table, record, request.Values, plane, ct);
            if (progress is not null) await progress(++done, records.Count, "jobs.stages.writing");
        }
        Declare("record.bulk_updated", table, null).With("count", records.Count);
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
        return new BulkResult(records.Count);
    }

    public async Task<BulkResult> BulkDeleteAsync(
        string tableName, BulkDeleteRequest request, RecordPlane plane, CancellationToken ct,
        int maxIds = MaxBulk, Progress? progress = null)
    {
        Bounded(request.Ids, maxIds);
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, RecordPlane.Admin, PublicOp.Read, ct);
        using var activity = Span("dcms.dynamicapp.mutation", model, table, "bulk-delete");
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var ids = request.Ids.Distinct().ToList();
        var records = await Records(table).Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        var removed = new HashSet<Guid>();
        var done = 0;
        foreach (var record in records.Where(r => !removed.Contains(r.Id)))
        {
            events.NextAction();
            await DeleteCoreAsync(model, table, record, removed, 0, ct, records.Count);
            if (progress is not null) await progress(++done, records.Count, "jobs.stages.writing");
        }
        Declare("record.bulk_deleted", table, null).With("count", records.Count).With("cascaded", removed.Count - records.Count);
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
        return new BulkResult(records.Count);
    }

    /// <summary>
    /// Creates many records of one table at once, all or nothing: every row is checked first — its
    /// values, its lookups, its unique values against the table and against the other rows — and
    /// nothing is written unless all pass, or when <paramref name="dryRun"/>. A lookup may name its
    /// target by id or by the target's primary field value (a company's name). Each record is
    /// created as one would be by hand, so row triggers fire for each.
    /// </summary>
    /// <param name="maxRows">The ceiling: <see cref="MaxImport"/> in a request, more in a background job.</param>
    /// <param name="maxCsvChars">The CSV ceiling, likewise.</param>
    public async Task<ImportResult> ImportAsync(
        string tableName, ImportRequest request, bool dryRun, CancellationToken ct,
        int maxRows = MaxImport, int maxCsvChars = CsvRows.MaxChars, Progress? progress = null)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, RecordPlane.Admin, PublicOp.Create, ct);
        var rows = request switch
        {
            { Csv: { } csv, Rows: null } => CsvRows.Parse(table, csv, request.Columns, maxRows, maxCsvChars),
            { Rows: { } given, Csv: null } => given,
            { CsvFile: { } file } => throw new ContractValidationException($"The file '{file}' was not sent: only the console can read an attached file."),
            _ => throw new ContractValidationException("Send rows or csv, not both."),
        };
        if (rows.Count == 0 || rows.Count > maxRows)
        {
            throw new ContractValidationException($"An import takes 1 to {maxRows} rows.");
        }
        // Checking every row is the first half of the work, writing them the second: all or
        // nothing either way, so the bar is one count over both.
        long work = rows.Count * 2L;
        using var activity = Span("dcms.dynamicapp.mutation", model, table, "import");
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var errors = new List<ImportError>();
        var names = await ResolveLookupNamesAsync(model, table, rows, ct);

        // Every row encoded first; then one query per lookup and one for unique values, for all rows
        // at once. Checked row by row they were a query each: 1,000 rows cost thousands of round trips.
        var rowErrors = new Dictionary<string, string>[rows.Count];
        var writes = new RecordCodec.WriteResult?[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            if (progress is not null && i % 100 == 0) await progress(i, work, "jobs.stages.validating");
            var values = rows[i].DeepClone().AsObject();
            rowErrors[i] = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var member in table.Members.Where(m => m.IsLookup))
            {
                if (values[member.ApiName] is JsonValue v && v.TryGetValue<string>(out var raw) && raw.Trim() is var text && !Guid.TryParse(text, out _))
                {
                    if (names.Find(member.Id, text, out var ambiguous) is { } target)
                    {
                        values[member.ApiName] = target.ToString();
                    }
                    else
                    {
                        rowErrors[i][member.ApiName] = ambiguous
                            ? $"more than one record is called '{text}'; use its id."
                            : $"there is no record called '{text}'.";
                    }
                }
            }
            try
            {
                writes[i] = RecordCodec.Write(table, values, null, RecordPlane.Admin);
            }
            catch (RecordValidationException e)
            {
                foreach (var (field, message) in e.Errors)
                {
                    rowErrors[i].TryAdd(field, message);
                }
            }
        }

        var encoded = writes.OfType<RecordCodec.WriteResult>().ToList();
        var missing = await MissingLookupsAsync(model, table, encoded, RecordPlane.Admin, ct);
        var wantedKeys = encoded.SelectMany(w => RecordCodec.UniqueKeys(table, w.Data)).Select(k => k.Key).Distinct().ToList();
        var taken = wantedKeys.Count == 0
            ? []
            : (await db.UniqueKeys.Where(k => k.InstanceId == InstanceId && wantedKeys.Contains(k.Key))
                    .Select(k => new { k.ConstraintId, k.Key }).ToListAsync(ct))
                .Select(k => (k.ConstraintId, k.Key)).ToHashSet();

        var claimed = new HashSet<(Guid, string)>();
        var accepted = new List<JsonObject>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (writes[i] is { } write)
            {
                var lookups = table.Members
                    .Where(m => m.IsLookup && write.Changed.Contains(m.ApiName)
                                && write.Data[m.Key] is JsonValue value && Guid.TryParse(value.GetValue<string>(), out var target)
                                && missing.Contains((m.Id, target)))
                    .ToList();
                foreach (var member in lookups)
                {
                    rowErrors[i].TryAdd(member.ApiName,
                        $"there is no {(model.ById.TryGetValue(member.Lookup!.TargetTableId, out var targetTable) ? targetTable.ApiName : "target")} record {write.Data[member.Key]!.GetValue<string>()}.");
                }
                if (lookups.Count == 0)
                {
                    foreach (var key in RecordCodec.UniqueKeys(table, write.Data))
                    {
                        if (!claimed.Add(key) || taken.Contains(key))
                        {
                            rowErrors[i]["_record"] = $"another {table.ApiName} record already has this {Describe(table, key.ConstraintId)}.";
                        }
                    }
                }
                if (rowErrors[i].Count == 0)
                {
                    accepted.Add(write.Data);
                }
            }
            errors.AddRange(rowErrors[i].Select(e => new ImportError(i + 1, e.Key, e.Value)));
        }
        activity?.SetTag("dcms.dynamicapp.rows", rows.Count);
        if (errors.Count > 0 || dryRun)
        {
            return new ImportResult(rows.Count, 0, dryRun, errors.Take(MaxImportErrors).ToList(), errors.Count);
        }

        var now = clock.GetUtcNow();
        var written = 0;
        foreach (var data in accepted)
        {
            if (progress is not null && written++ % 100 == 0) await progress(rows.Count + written, work, "jobs.stages.writing");
            var record = new AppRecord
            {
                Id = Guid.NewGuid(),
                InstanceId = InstanceId,
                TableId = table.Id,
                Data = data.ToJsonString(),
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = Actor,
                UpdatedBy = Actor,
            };
            db.Records.Add(record);
            foreach (var (constraintId, key) in RecordCodec.UniqueKeys(table, data))
            {
                db.UniqueKeys.Add(new AppUniqueKey { Id = Guid.NewGuid(), InstanceId = InstanceId, ConstraintId = constraintId, RecordId = record.Id, Key = key });
            }
            events.NextAction();
            events.Add(db, model.Revision.Number, AppEvent.RowCreated, Entity(table, record.Id),
                new JsonObject { ["record"] = RecordCodec.Read(table, record, RecordPlane.System) });
        }
        Declare("record.imported", table, null).With("count", accepted.Count);
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
        return new ImportResult(rows.Count, accepted.Count, false, [], 0);
    }

    /// <summary>
    /// The records an import's lookups name by their primary field: (lookup, name) → id, or null
    /// when more than one record has the name. Names match without regard to case; when that is
    /// ambiguous, the record spelled exactly so wins, so "Acme S.R.L." and "Acme S.r.l." (both
    /// allowed by a case-sensitive unique field) can each be named.
    /// </summary>
    private sealed class LookupNames
    {
        public Dictionary<(Guid, string), Guid?> Folded { get; } = [];
        public Dictionary<(Guid, string), Guid?> Exact { get; } = [];

        public void Add(Guid member, string name, Guid id)
        {
            Folded[(member, name.ToLowerInvariant())] = Folded.ContainsKey((member, name.ToLowerInvariant())) ? null : id;
            Exact[(member, name)] = Exact.ContainsKey((member, name)) ? null : id;
        }

        public Guid? Find(Guid member, string name, out bool ambiguous)
        {
            ambiguous = false;
            if (Folded.TryGetValue((member, name.ToLowerInvariant()), out var folded) && folded is { } id)
            {
                return id;
            }
            if (Exact.TryGetValue((member, name), out var exact) && exact is { } exactId)
            {
                return exactId;
            }
            ambiguous = Folded.ContainsKey((member, name.ToLowerInvariant()));
            return null;
        }
    }

    private async Task<LookupNames> ResolveLookupNamesAsync(RuntimeModel model, RuntimeTable table, IReadOnlyList<JsonObject> rows, CancellationToken ct)
    {
        var found = new LookupNames();
        foreach (var member in table.Members.Where(m => m.IsLookup))
        {
            var wanted = rows.Select(r => r[member.ApiName] is JsonValue v && v.TryGetValue<string>(out var t) ? t.Trim() : null)
                .Where(t => t is { Length: > 0 and <= 1000 } && !Guid.TryParse(t, out _))
                .Select(t => t!.ToLowerInvariant()).Distinct().ToArray();
            if (wanted.Length == 0 || !model.ById.TryGetValue(member.Lookup!.TargetTableId, out var target) || target.Primary is not { } primary)
            {
                continue;
            }
            // Raw SQL by construction: identifiers are this method's own, every value a parameter.
            const string sql = """
                SELECT r.* FROM apps.records r
                WHERE r."InstanceId" = @instance AND r."TableId" = @table AND lower(r."Data" ->> @key) = ANY(@names)
                """;
            var matches = await db.Records.FromSqlRaw(sql,
                    new NpgsqlParameter("instance", NpgsqlDbType.Uuid) { Value = InstanceId },
                    new NpgsqlParameter("table", NpgsqlDbType.Uuid) { Value = target.Id },
                    new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = primary.Key },
                    new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = wanted })
                .IgnoreQueryFilters() // the instance is in the WHERE, under the caller's RlsScope
                .AsNoTracking()
                .ToListAsync(ct);
            foreach (var match in matches)
            {
                var name = (JsonNode.Parse(match.Data)?[primary.Key] as JsonValue)?.GetValue<object>()?.ToString();
                if (name is not null)
                {
                    found.Add(member.Id, name, match.Id);
                }
            }
        }
        return found;
    }

    /// <summary>Links two records through a many-to-many relationship. Linking twice is not an error.</summary>
    /// <returns>False when either record does not exist.</returns>
    public async Task<bool> LinkAsync(string tableName, Guid id, string navigation, Guid targetId, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, RecordPlane.Admin, PublicOp.Read, ct);
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
            events.Add(db, model.Revision.Number, AppEvent.RelationCreated, Entity(table, id),
                Link(model, nav, source, target));
            Declare("relation.linked", table, id).With("relationship", nav.Relationship.ApiName).With("target", targetId);
            await SaveAsync(ct);
        }
        return true;
    }

    /// <returns>False when there was no such link.</returns>
    public async Task<bool> UnlinkAsync(string tableName, Guid id, string navigation, Guid targetId, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (model, table) = await TableAsync(tableName, RecordPlane.Admin, PublicOp.Read, ct);
        var nav = ManyToMany(table, navigation);
        var (source, target) = nav.FromSource ? (id, targetId) : (targetId, id);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        Declare("relation.unlinked", table, id).With("relationship", nav.Relationship.ApiName).With("target", targetId);
        // Set-based, and recorded: the declared entry above names the link that goes.
        var removed = await db.RelationLinks
            .Where(l => l.InstanceId == InstanceId && l.RelationshipId == nav.Relationship.Id && l.SourceId == source && l.TargetId == target)
            .ExecuteDeleteAsync(ct);
        if (removed == 0)
        {
            return false;
        }
        events.Add(db, model.Revision.Number, AppEvent.RelationDeleted, Entity(table, id), Link(model, nav, source, target));
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
        return true;
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
        await CheckLookupsAsync(model, table, write.Data, write.Changed, plane, ct);
        record.Data = write.Data.ToJsonString();
        record.Version++;
        record.UpdatedAt = clock.GetUtcNow();
        record.UpdatedBy = Actor;
        await ClaimKeysAsync(table, record.Id, write.Data, ct);

        var previous = new JsonObject();
        foreach (var name in write.Changed)
        {
            previous[name] = existing[table.ByName[name].Key]?.DeepClone();
        }
        events.Add(db, model.Revision.Number, AppEvent.RowUpdated, Entity(table, record.Id),
            new JsonObject { ["record"] = RecordCodec.Read(table, record, RecordPlane.System), ["previous"] = previous },
            write.Changed);
    }

    /// <summary>
    /// Deletes a record and does what each relationship pointing at it says: refuse
    /// (restrict), clear the pointer (set null), or delete the pointing records too (cascade),
    /// up to <see cref="MaxCascade"/> records and <see cref="MaxCascadeDepth"/> hops.
    /// </summary>
    /// <param name="requested">How many records the caller asked to delete: the cascade allowance
    /// is on top of those, so a bulk delete of 600 is not mistaken for a cascade of 600.</param>
    private async Task DeleteCoreAsync(
        RuntimeModel model, RuntimeTable table, AppRecord record, HashSet<Guid> removed, int depth, CancellationToken ct,
        int requested = 1)
    {
        if (!removed.Add(record.Id))
        {
            return;
        }
        if (removed.Count > MaxCascade + requested - 1 || depth > MaxCascadeDepth)
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
                        var was = data[relationship.Id.ToString()]?.DeepClone();
                        data.Remove(relationship.Id.ToString());
                        r.Data = data.ToJsonString();
                        r.Version++;
                        r.UpdatedAt = clock.GetUtcNow();
                        r.UpdatedBy = Actor;
                        events.Add(db, model.Revision.Number, AppEvent.RowUpdated, Entity(source, r.Id),
                            new JsonObject
                            {
                                ["record"] = RecordCodec.Read(source, r, RecordPlane.System),
                                ["previous"] = new JsonObject { [relationship.ApiName] = was },
                            },
                            [relationship.ApiName]);
                    }
                    break;
                default:
                    foreach (var r in pointing)
                    {
                        await DeleteCoreAsync(model, source, r, removed, depth + 1, ct, requested);
                    }
                    break;
            }
        }

        events.Add(db, model.Revision.Number, AppEvent.RowDeleted, Entity(table, record.Id),
            new JsonObject { ["record"] = RecordCodec.Read(table, record, RecordPlane.System) });
        db.Records.Remove(record);
        // Set-based, under the record.deleted entry the caller declares.
        await db.UniqueKeys.Where(k => k.InstanceId == InstanceId && k.RecordId == record.Id).ExecuteDeleteAsync(ct);
        await db.RelationLinks.Where(l => l.InstanceId == InstanceId && (l.SourceId == record.Id || l.TargetId == record.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// The lookup targets the writes point at that do not exist (or, from the site, cannot be read
    /// there): CheckLookupsAsync for many records, one query per lookup.
    /// </summary>
    private async Task<HashSet<(Guid Member, Guid Target)>> MissingLookupsAsync(RuntimeModel model, RuntimeTable table,
        IReadOnlyList<RecordCodec.WriteResult> writes, RecordPlane plane, CancellationToken ct)
    {
        var missing = new HashSet<(Guid, Guid)>();
        foreach (var member in table.Members.Where(m => m.IsLookup))
        {
            var targets = writes
                .Where(w => w.Changed.Contains(member.ApiName))
                .Select(w => w.Data[member.Key] is JsonValue value && Guid.TryParse(value.GetValue<string>(), out var id) ? id : (Guid?)null)
                .OfType<Guid>().Distinct().ToList();
            if (targets.Count == 0)
            {
                continue;
            }
            var found = model.ById.TryGetValue(member.Lookup!.TargetTableId, out var targetTable)
                ? (await (Readable(targetTable, plane) ? Records(model, targetTable, plane) : Records(targetTable))
                    .Where(r => targets.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct)).ToHashSet()
                : [];
            missing.UnionWith(targets.Where(t => !found.Contains(t)).Select(t => (member.Id, t)));
        }
        return missing;
    }

    /// <summary>Every lookup the write changed points at an existing record of its target table.</summary>
    private async Task CheckLookupsAsync(RuntimeModel model, RuntimeTable table, JsonObject data, IReadOnlyList<string> changed, RecordPlane plane, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in table.Members.Where(m => m.IsLookup && changed.Contains(m.ApiName)))
        {
            if (data[member.Key] is not JsonValue value || !Guid.TryParse(value.GetValue<string>(), out var target))
            {
                continue;
            }
            // From the site, a lookup may only point at a record the caller can read there: else a
            // row rule that follows it would let them file records under, say, any company.
            if (!model.ById.TryGetValue(member.Lookup!.TargetTableId, out var targetTable)
                || !await (Readable(targetTable, plane) ? Records(model, targetTable, plane) : Records(targetTable)).AnyAsync(r => r.Id == target, ct))
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
            throw ChangedElsewhere("The record changed while you were editing it. Read it again and retry.");
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
            if (!model.ById.TryGetValue(member.Lookup!.TargetTableId, out var target) || !Readable(target, plane))
            {
                continue; // left as the id: the site may not read that table
            }
            var ids = rows.Select(r => (JsonNode.Parse(r.Data)?[member.Key] as JsonValue)?.GetValue<string>())
                .Where(s => s is not null).Select(s => Guid.Parse(s!)).Distinct().ToList();
            var targets = await Records(model, target, plane).AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            foreach (var item in items)
            {
                if (item[name] is JsonValue v && Guid.TryParse(v.GetValue<string>(), out var id) && targets.TryGetValue(id, out var hit))
                {
                    item[name] = RecordCodec.Read(target, hit, plane);
                }
            }
        }
    }

    /// <summary>A table the public plane may read, as its records' reads would find it: 404, 403 or 401 otherwise.</summary>
    public Task<(RuntimeModel Model, RuntimeTable Table)> ReadableTableAsync(string tableName, CancellationToken ct) =>
        TableAsync(tableName, RecordPlane.Public, PublicOp.Read, ct);

    private static readonly System.Text.Json.JsonSerializerOptions FilterJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// "The current user" in a filter (#302) — a shared view's, or a site's own query — as whoever
    /// is asking: on the site the signed-in user as row rules see them (an email and groups only
    /// from a User Authentication sign-in), in the console the member. Same tree as a view's filter.
    /// </summary>
    private RecordFilter? WithViewer(RecordFilter? filter, RecordPlane plane)
    {
        if (filter is null) return null;
        var view = System.Text.Json.JsonSerializer.Deserialize<ViewFilter>(System.Text.Json.JsonSerializer.Serialize(filter, FilterJson), FilterJson);
        if (!UserValue.Mentioned(view)) return filter;
        var viewer = plane == RecordPlane.Public
            ? Subject is { } s ? new ViewUser(s.Id.ToString(), s.Email, s.Groups.Select(g => g.ToString()).ToList()) : null
            : ViewUser.Member(http.HttpContext?.User);
        return System.Text.Json.JsonSerializer.Deserialize<RecordFilter>(
            System.Text.Json.JsonSerializer.Serialize(UserValue.Resolve(view, viewer, NoRecord("in"), NoRecord("nin")), FilterJson), FilterJson);
    }

    /// <summary>
    /// "Nothing" and "everything" in this filter language, which refuses an empty and/or from a
    /// caller: no record has the empty id.
    /// </summary>
    private static ViewFilter NoRecord(string op) => new() { Field = "id", Op = op, Value = new JsonArray(Guid.Empty.ToString()) };

    private async Task<(RuntimeModel Model, RuntimeTable Table)> TableAsync(string tableName, RecordPlane plane, PublicOp op, CancellationToken ct)
    {
        var model = await models.GetAsync(ct);
        var table = model?.Table(tableName);
        if (table is null)
        {
            throw plane == RecordPlane.Public
                ? new PublicAccessException(404, $"There is no table '{tableName}'.")
                : new ContractValidationException(model is null ? "This application has no published tables yet." : $"There is no table '{tableName}'.");
        }
        if (plane == RecordPlane.Public)
        {
            Allow(table, op);
        }
        return (model!, table);
    }

    private IQueryable<AppRecord> Records(RuntimeTable table)
    {
        var instanceId = InstanceId;
        return db.Records.Where(r => r.InstanceId == instanceId && r.TableId == table.Id);
    }

    /// <summary>The records the plane may <paramref name="op"/>: on the public site, what <see cref="Scope"/> leaves.</summary>
    private IQueryable<AppRecord> Records(RuntimeModel model, RuntimeTable table, RecordPlane plane, PublicOp op = PublicOp.Read)
    {
        var records = Records(table);
        if (Scope(model, table, plane, op) is not { } scope)
        {
            return records;
        }
        // Raw SQL by construction: RowRules writes only its own identifiers, every value is a
        // parameter. Named apart from the parameters EF adds to the query it is composed into.
        var parameters = new List<NpgsqlParameter>();
        var prefix = $"rs{++_scopes}_";
        string Parameter(object value, NpgsqlDbType type)
        {
            var p = new NpgsqlParameter($"{prefix}{parameters.Count}", type) { Value = value };
            parameters.Add(p);
            return "@" + p.ParameterName;
        }
        var sql = $"""
            SELECT r.* FROM apps.records r WHERE r."InstanceId" = {Parameter(InstanceId, NpgsqlDbType.Uuid)}
            AND r."TableId" = {Parameter(table.Id, NpgsqlDbType.Uuid)} AND {scope(Parameter)}
            """;
        var reachable = db.Records.FromSqlRaw(sql, parameters.ToArray())
            .IgnoreQueryFilters() // only ids come out, matched against Records(table), which keeps the tenant filter
            .Select(r => r.Id);
        return records.Where(r => reachable.Contains(r.Id));
    }

    /// <summary>
    /// Which of a table's records the public site may <paramref name="op"/>, as SQL over alias
    /// <c>r</c>; null when it may reach them all. Records the visitor owns, where the table's
    /// own-record access allows, and those a row rule granting the action matches for the
    /// signed-in user. A member holding the action reaches every record, unless the table has row
    /// rules and their role does not bypass them: then the action reaches the rows the rules let
    /// them read, as well as those granted for the action itself.
    /// </summary>
    private SqlWriter? Scope(RuntimeModel model, RuntimeTable table, RecordPlane plane, PublicOp op)
    {
        var access = table.Def.Public;
        var member = Member(table, op);
        if (plane != RecordPlane.Public || Unscoped(table, op) || (access.Read == PublicRead.All && (op == PublicOp.Read || member)))
        {
            return null;
        }
        bool Own(PublicOp o) => o switch
        {
            PublicOp.Read => access.Read == PublicRead.Own,
            PublicOp.Update => access.UpdateOwn,
            PublicOp.Delete => access.DeleteOwn,
            _ => false,
        };
        var own = Own(op) || (member && Own(PublicOp.Read));
        var rules = access.Rules.Where(r => Grants(r, op) || (member && r.Read)).ToList();
        var (visitor, subject, tenantId, instanceId) = (Visitor, Subject, context.TenantId, InstanceId);
        return parameter =>
        {
            var terms = new List<string>();
            if (own && visitor is { } v)
            {
                terms.Add($"""r."OwnerVisitorId" = {parameter(v, NpgsqlDbType.Uuid)}""");
            }
            if (subject is not null)
            {
                terms.AddRange(rules.Select(rule => RowRules.Sql(model, table, rule, subject, "r", tenantId, instanceId, parameter)));
            }
            return terms.Count == 0 ? "FALSE" : "(" + string.Join(" OR ", terms) + ")";
        };
    }

    /// <summary>
    /// A site update to a table with row rules must leave the record where the writer can still
    /// change it, as a database policy's WITH CHECK would: otherwise a user could hand a record out
    /// of their reach. Runs inside the write's transaction, so a refusal undoes it.
    /// </summary>
    /// <remarks>
    /// A create is not checked. The site creates only where the table allows it (its public create,
    /// or a role holding create), and a create-only table is meant to take records the writer never
    /// sees again, e.g. web-to-lead; what a create may point at is held to what the writer can read
    /// by <see cref="CheckLookupsAsync"/>, so it cannot file a record under someone else's.
    /// </remarks>
    private async Task CheckReachAsync(RuntimeModel model, RuntimeTable table, Guid id, RecordPlane plane, CancellationToken ct)
    {
        if (plane != RecordPlane.Public || table.Def.Public.Rules.Count == 0 || Unscoped(table, PublicOp.Update)
            || Scope(model, table, plane, PublicOp.Update) is null || await Records(model, table, plane, PublicOp.Update).AnyAsync(r => r.Id == id, ct))
        {
            return;
        }
        throw new PublicAccessException(403, $"That would leave the {table.ApiName} record out of your reach.");
    }

    private static bool Grants(RowRule rule, PublicOp op) => op switch
    {
        PublicOp.Read => rule.Read,
        PublicOp.Update => rule.Update,
        PublicOp.Delete => rule.Delete,
        _ => false,
    };

    private bool Readable(RuntimeTable table, RecordPlane plane) =>
        plane != RecordPlane.Public || PublicApi.Readable(table) || Member(table, PublicOp.Read);

    private enum PublicOp { Read, Create, Update, Delete }

    /// <summary>
    /// What the public site may do with a table, decided by its public access. A table it may
    /// not read does not exist as far as the site can tell (404); one it may read but not write
    /// says so (403); "own" or rule access without a signed-in visitor asks for one (401).
    /// </summary>
    private void Allow(RuntimeTable table, PublicOp op)
    {
        if (Member(table, op))
        {
            return;
        }
        var access = table.Def.Public;
        // What the caller can see of the table decides how a refusal reads: 404 hides it, 403 does not.
        var readable = Readable(table, RecordPlane.Public);
        var allowed = op switch
        {
            PublicOp.Read => readable,
            PublicOp.Create => access.Create,
            PublicOp.Update => access.UpdateOwn || access.Rules.Any(r => r.Update),
            _ => access.DeleteOwn || access.Rules.Any(r => r.Delete),
        };
        if (!allowed)
        {
            throw readable || access.Create || Member(table, PublicOp.Create)
                ? new PublicAccessException(403, $"The site may not {op.ToString().ToLowerInvariant()} {table.ApiName} records.")
                : new PublicAccessException(404, $"There is no table '{table.ApiName}'.");
        }
        var needsVisitor = op is PublicOp.Update or PublicOp.Delete || (op == PublicOp.Read && access.Read != PublicRead.All);
        if (needsVisitor && Visitor is null)
        {
            throw new PublicAccessException(401, $"Sign in to {op.ToString().ToLowerInvariant()} your {table.ApiName} records.");
        }
    }

    private static RuntimeNavigation Navigation(RuntimeTable table, string name) =>
        table.Navigations.GetValueOrDefault(name) ?? throw new ContractValidationException($"{table.ApiName} has no relationship '{name}'.");

    private static RuntimeNavigation ManyToMany(RuntimeTable table, string name) =>
        Navigation(table, name) is { Kind: NavigationKind.ManyToMany } nav
            ? nav
            : throw new ContractValidationException($"'{name}' is a lookup; set it on the record instead of linking.");

    private static AppEventEntity Entity(RuntimeTable table, Guid id) => new(table.ApiName, id);

    private static JsonObject Link(RuntimeModel model, RuntimeNavigation nav, Guid source, Guid target) => new()
    {
        ["relationship"] = nav.Relationship.ApiName,
        ["sourceTable"] = model.ById.GetValueOrDefault(nav.Relationship.SourceTableId)?.ApiName,
        ["sourceId"] = source.ToString(),
        ["targetTable"] = model.ById.GetValueOrDefault(nav.Relationship.TargetTableId)?.ApiName,
        ["targetId"] = target.ToString(),
    };

    private static string Pointer(Guid relationshipId, Guid target) =>
        new JsonObject { [relationshipId.ToString()] = target.ToString() }.ToJsonString();

    private static void Bounded(IReadOnlyList<Guid> ids, int max = MaxBulk)
    {
        if (ids.Count == 0 || ids.Count > max)
        {
            throw new ContractValidationException($"A bulk operation takes 1 to {max} ids.");
        }
    }

    private static ContractConflictException Stale(AppRecord record) =>
        ChangedElsewhere($"The record changed since you read it; it is at version {record.Version}. Read it again and retry.");

    private const string ChangedElsewhereKey = "dcms.dynamicapp.changed-elsewhere";

    /// <summary>
    /// A conflict that reading again resolves — another write got to the record first — as against
    /// one that will repeat (a unique value taken). Flows retry the first kind (FlowExecutor).
    /// </summary>
    private static ContractConflictException ChangedElsewhere(string message)
    {
        var e = new ContractConflictException(message);
        e.Data[ChangedElsewhereKey] = true;
        return e;
    }

    public static bool IsChangedElsewhere(Exception e) => e.Data.Contains(ChangedElsewhereKey);

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
