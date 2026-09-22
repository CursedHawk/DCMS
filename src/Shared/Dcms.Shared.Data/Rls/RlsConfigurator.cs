using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Data.Rls;

/// <summary>
/// Applies the Row-Level Security backstop to the tenant-scoped tables after the
/// EF migrations have created them. Defense-in-depth on top of the EF per-tenant
/// query filters (the primary isolation guarantee). Idempotent — safe to run on
/// every startup. See <c>infra/postgres/init/01-rls.sql</c> and ADR 0003.
///
/// Each table gets two permissive policies, which Postgres ORs together:
/// <c>tenant_isolation</c> admits a row whose <c>TenantId</c> equals the
/// <c>app.tenant_id</c> GUC, and <c>platform_scope</c> admits any row while the
/// <c>app.scope</c> GUC is <c>platform</c> — the explicit, per-operation widening that
/// replaces <c>IgnoreQueryFilters()</c> once the services stop being the table owner
/// (ADR 0015).
///
/// <para>Today the app still connects as the owner and bypasses RLS, so this constrains
/// only a non-owner: <c>dcms_rls</c>, which the isolation test uses, and <c>dcms_app</c>,
/// which is the runtime role the services move onto one at a time in ADR 0015 phase 4.
/// Both are granted here as each table is protected.</para>
/// </summary>
public static class RlsConfigurator
{
    // (schema, table) pairs whose rows carry a "TenantId" column and are
    // tenant-filtered in EF. Cross-tenant scan tables (content_outbox,
    // scheduled_publishes) are deliberately excluded.
    public static readonly IReadOnlyList<(string Schema, string Table)> TenantTables =
    [
        ("tenancy", "domains"),
        ("tenancy", "tenant_memberships"),
        ("tenancy", "tenant_roles"),
        ("tenancy", "tenant_role_permissions"),
        ("tenancy", "member_roles"),
        ("tenancy", "invitations"),
        ("plugins", "plugin_instances"),
        ("cms", "content_items"),
        ("cms", "content_versions"),
        ("media", "media_assets"),
        ("media", "media_variants"),
        ("media", "media_folders"),
        ("sites", "sites"),
        ("sites", "site_builds"),
        ("sites", "site_drafts"),
        ("ai", "tenant_ai_settings"),
        // Holds every user's Vault-Transit-encrypted provider key. Added a release after its
        // sibling above and missed here, which mattered because 01-rls.sql grants SELECT on
        // every *future* table in these schemas by default while the policies are opt-in per
        // table — so the omission was a grant with nothing enforcing the tenant predicate.
        ("ai", "user_ai_settings"),
        // Assistant transcripts. `messages` carries a denormalised TenantId for exactly this
        // reason: a policy on the parent does nothing for the child, and these rows hold whole
        // tool results — draft content, analytics figures — lifted out of the tenant's data.
        ("ai", "conversations"),
        ("ai", "messages"),
        ("ai", "runs"),
        ("search", "search_documents"),
        ("analytics", "events"),
        ("analytics", "daily_rollups"),
        ("visitors", "visitor_accounts"),
        ("visitors", "visitor_refresh_tokens"),
        ("chat", "conversations"),
        ("chat", "messages"),
        ("forms", "form_submissions"),
        // Every table here holds or gates access to a tenant's Meta OAuth tokens.
        // meta_oauth_states is tenant-filtered like the rest even though the OAuth
        // callback reads it with IgnoreQueryFilters: the callback is anonymous and has
        // no tenant context, so that row is what *establishes* the tenant. RLS still
        // applies to it for any non-owner reader, which is the point.
        ("social", "meta_connections"),
        ("social", "meta_sync_states"),
        ("social", "meta_media_map"),
        ("social", "meta_oauth_states"),
        // Both notification tables are per-tenant and hold, in ParamsJson, interpolation
        // values lifted from tenant content (site names, member emails). They are also a
        // worked example of why this list is checked at startup: recipients is written by a
        // background consumer with no ambient tenant, so the query filter is not the thing
        // standing between one tenant's bell and another's.
        ("notifications", "notifications"),
        ("notifications", "recipients"),
        // Audit rows carry a TenantId and must be covered like any other tenant table.
        // Guid.Empty marks platform-scope records (logins, tenant provisioning): the policy
        // compares equality against the GUC, so those rows are invisible to tenant readers,
        // which is the intended behaviour. The sibling audit tables (chain_heads,
        // chain_anchors, audit_outbox) are deliberately excluded — cross-tenant scan tables,
        // same rationale as content_outbox above.
        ("audit", "audit_events"),
    ];

    /// <summary>
    /// Tables that carry a <c>TenantId</c> and are deliberately left without a policy, because
    /// the code that reads them is a cross-tenant scan and a per-tenant predicate would break
    /// it. Kept as data rather than as a comment so <see cref="AssertCoverage"/> can tell
    /// "decided against" apart from "not noticed".
    /// </summary>
    public static readonly IReadOnlyList<(string Schema, string Table)> ExemptTables =
    [
        ("cms", "content_outbox"),        // drained by a cross-tenant dispatcher
        ("cms", "scheduled_publishes"),   // scanned across tenants by the publish worker
        ("audit", "audit_outbox"),        // same, and mapped into every business context
        ("audit", "chain_heads"),         // one row per chain; the verifier walks all of them
        ("audit", "chain_anchors"),
    ];

    /// <summary>
    /// Fails startup when a tenant-scoped table is in neither list.
    ///
    /// <para><b>Why this is worth a startup check.</b> <c>TenantTables</c> is hand-maintained,
    /// and the failure mode when somebody forgets an entry is silent: the table still works,
    /// the tests still pass, and the only thing that changed is that the backstop no longer
    /// covers it. <c>ai.user_ai_settings</c> sat that way — the table holding every user's
    /// encrypted provider key was the one table in the granted schemas with a grant and no
    /// policy. A new tenant table is exactly when nobody is thinking about RLS, so the check
    /// runs then rather than relying on it being remembered.</para>
    ///
    /// <para>Pass every business <see cref="DbContext"/>; each contributes its own model.</para>
    /// </summary>
    public static void AssertCoverage(IEnumerable<DbContext> contexts, ILogger logger)
    {
        var known = new HashSet<(string, string)>(TenantTables);
        known.UnionWith(ExemptTables);

        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var context in contexts)
        {
            var model = context.Model;
            foreach (var entity in model.GetEntityTypes())
            {
                // Only real tables, and only the ones carrying the tenant discriminator. An
                // owned type or a keyless projection has no table of its own to protect.
                if (entity.FindProperty(nameof(TenantEntity.TenantId)) is null)
                {
                    continue;
                }
                if (entity.GetTableName() is not { } table)
                {
                    continue;
                }
                var schema = entity.GetSchema() ?? model.GetDefaultSchema();
                if (schema is null)
                {
                    continue;
                }
                if (!known.Contains((schema, table)))
                {
                    missing.Add($"{schema}.{table}");
                }
            }
        }

        if (missing.Count == 0)
        {
            logger.LogInformation(
                "RLS coverage verified: {Covered} protected, {Exempt} deliberately exempt.",
                TenantTables.Count, ExemptTables.Count);
            return;
        }

        throw new InvalidOperationException(
            $"Refusing to start: tenant-scoped table(s) {string.Join(", ", missing)} have a " +
            "TenantId but no Row-Level Security policy. Add each to RlsConfigurator.TenantTables, " +
            "or to ExemptTables with a comment saying why a cross-tenant scan needs it unfiltered.");
    }

    public static async Task ApplyAsync(DbContext context, ILogger logger, CancellationToken ct = default)
    {
        foreach (var (schema, table) in TenantTables)
        {
            await ProtectAsync(context, schema, table, logger, ct);
        }

        // Partitions do not inherit their parent's protection for a query aimed straight at
        // them, so each is protected in its own right. See PartitionsAsync.
        foreach (var (schema, table) in await PartitionsAsync(context, TenantTables, ct))
        {
            await ProtectAsync(context, schema, table, logger, ct);
        }

        await AssertAppliedAsync(context, logger, ct);
    }

    /// <summary>
    /// Enables row security on one table and (re)creates the tenant policy on it. Idempotent,
    /// and safe on a table that is already protected.
    ///
    /// <para>Public because a partition created after startup — <c>AuditSchemaConfigurator</c>
    /// makes one whenever the maintenance worker rolls a month — has to be protected when it is
    /// created, not at the next restart.</para>
    /// </summary>
    public static async Task ProtectAsync(
        DbContext context, string schema, string table, ILogger logger, CancellationToken ct = default)
    {
        var qualified = $"\"{schema}\".\"{table}\"";
        // The policies are the important part — apply them unconditionally.
        // CREATE POLICY has no IF NOT EXISTS; drop-then-create keeps it idempotent.
        //
        // Two PERMISSIVE policies, which Postgres ORs together. tenant_isolation is the one
        // from ADR 0005 and is unchanged. platform_scope is ADR 0015's replacement for
        // IgnoreQueryFilters(): once the services connect as a NOBYPASSRLS role, ignoring the
        // EF filter stops widening anything, and the paths that legitimately cross tenants —
        // the outbox dispatchers, the publish worker, SuperAdmin listings, tenant resolution
        // itself — need a way to say so. Saying it as a GUC keeps the decision at the call
        // site that knows, rather than at the registration that does not.
        var policySql = $"""
            ALTER TABLE {qualified} ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON {qualified};
            CREATE POLICY tenant_isolation ON {qualified}
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
            DROP POLICY IF EXISTS platform_scope ON {qualified};
            CREATE POLICY platform_scope ON {qualified}
                USING (current_setting('app.scope', true) = 'platform')
                WITH CHECK (current_setting('app.scope', true) = 'platform');
            """;
        try
        {
            await context.Database.ExecuteSqlRawAsync(policySql, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RLS policy apply failed for {Table}.", qualified);
            return;
        }

        // Grants to the least-privilege roles are best-effort: each exists only where
        // infra/postgres/init/ ran. dcms_rls reads (the isolation test); dcms_app is ADR
        // 0015's runtime role and needs DML, since it is what the services become.
        //
        // The bootstrap job grants both on whole schemas already. Repeating it per table is
        // for the table that did not exist when that job last ran — a migration creates it
        // moments later, and ALTER DEFAULT PRIVILEGES covers that, so this is the belt to
        // those braces.
        var grantSql = $"GRANT USAGE ON SCHEMA \"{schema}\" TO dcms_rls; GRANT SELECT ON {qualified} TO dcms_rls;";
        try
        {
            await context.Database.ExecuteSqlRawAsync(grantSql, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "RLS grant to dcms_rls skipped for {Table} (role absent).", qualified);
        }

        var appGrantSql =
            $"GRANT USAGE ON SCHEMA \"{schema}\" TO dcms_app; GRANT SELECT, INSERT, UPDATE, DELETE ON {qualified} TO dcms_app;";
        try
        {
            await context.Database.ExecuteSqlRawAsync(appGrantSql, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Grant to dcms_app skipped for {Table} (role absent).", qualified);
        }
    }

    /// <summary>
    /// Every partition, at any depth, of the given tables.
    ///
    /// <para><b>Why this is not a detail.</b> Postgres applies a partitioned table's row-security
    /// policy to queries that go <i>through the parent</i>. A query aimed straight at a partition
    /// is checked against that partition's own policies — and enabling row security on the parent
    /// enables nothing on the children. Meanwhile <c>infra/postgres/init/01-rls.sql</c> holds an
    /// ALTER DEFAULT PRIVILEGES that grants <c>dcms_rls</c> SELECT on every table <c>dcms</c>
    /// creates in these schemas, and each monthly audit partition is one of those. So
    /// <c>audit.audit_events</c> was protected while <c>SELECT * FROM audit.audit_events_2026m09</c>
    /// returned every tenant's records to the one role that exists to prove it cannot.</para>
    /// </summary>
    private static async Task<List<(string Schema, string Table)>> PartitionsAsync(
        DbContext context, IReadOnlyList<(string Schema, string Table)> parents, CancellationToken ct)
    {
        const string sql = """
            WITH RECURSIVE listed AS (
                SELECT c.oid
                FROM unnest(@schemas, @tables) AS w(schema_name, table_name)
                JOIN pg_namespace n ON n.nspname = w.schema_name
                JOIN pg_class c ON c.relnamespace = n.oid AND c.relname = w.table_name
            ),
            parts AS (
                SELECT i.inhrelid AS oid FROM pg_inherits i JOIN listed l ON i.inhparent = l.oid
                UNION ALL
                SELECT i.inhrelid FROM pg_inherits i JOIN parts p ON i.inhparent = p.oid
            )
            SELECT n.nspname, c.relname
            FROM parts
            JOIN pg_class c ON c.oid = parts.oid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            """;

        var found = new List<(string, string)>();
        await using var command = await CommandAsync(context, sql, parents, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found.Add((reader.GetString(0), reader.GetString(1)));
        }
        return found;
    }

    /// <summary>
    /// Asks Postgres which of the listed tables are actually protected, and refuses to finish
    /// the migration if any is not.
    ///
    /// <para><b>Why the count in the log was never evidence.</b> "applied to N tenant tables"
    /// is <c>TenantTables.Count</c> — it says how long the array is, not what the database
    /// did with it. And the loop above deliberately continues past a failure, so one table
    /// whose <c>ALTER</c>/<c>CREATE POLICY</c> did not take (a lock timeout, a table a
    /// migration had not created yet, a rename) left a WARNING in the log, a table with a
    /// <c>dcms_rls</c> SELECT grant, and no policy behind it. The grants are default-ALLOW and
    /// the policies opt-in, so that combination is readable unfiltered across every tenant —
    /// which is the whole failure this class exists to prevent.</para>
    ///
    /// <para>This reads <c>pg_class.relrowsecurity</c> and <c>pg_policy</c>: the database's own
    /// answer rather than the application's intention.</para>
    /// </summary>
    public static async Task AssertAppliedAsync(DbContext context, ILogger logger, CancellationToken ct = default)
    {
        const string sql = """
            SELECT n.nspname || '.' || c.relname
            FROM unnest(@schemas, @tables) AS w(schema_name, table_name)
            LEFT JOIN pg_namespace n ON n.nspname = w.schema_name
            LEFT JOIN pg_class c ON c.relnamespace = n.oid AND c.relname = w.table_name
            WHERE c.oid IS NULL
               OR NOT c.relrowsecurity
               OR NOT EXISTS (
                   SELECT 1 FROM pg_policy p
                   WHERE p.polrelid = c.oid AND p.polname = 'tenant_isolation')
               OR NOT EXISTS (
                   SELECT 1 FROM pg_policy p
                   WHERE p.polrelid = c.oid AND p.polname = 'platform_scope')
            """;

        // Partitions are checked alongside their parents, for the same reason they are
        // protected alongside them: a query aimed at one is not checked against the other.
        List<(string Schema, string Table)> targets = [.. TenantTables, .. await PartitionsAsync(context, TenantTables, ct)];

        var unprotected = new SortedSet<string>(StringComparer.Ordinal);
        await using (var command = await CommandAsync(context, sql, targets, ct))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                unprotected.Add(reader.GetString(0));
            }
        }

        if (unprotected.Count > 0)
        {
            throw new InvalidOperationException(
                $"Row-Level Security is NOT in force on {string.Join(", ", unprotected)}. Each is " +
                "listed in RlsConfigurator.TenantTables (or is a partition of one) and was granted " +
                "to dcms_rls, so leaving it without a tenant_isolation policy would expose its rows " +
                "unfiltered across every tenant. Check the RLS warnings logged above this line.");
        }

        logger.LogInformation(
            "Row-Level Security verified in the catalogue on all {Count} tenant tables and partitions.",
            targets.Count);
    }

    /// <summary>
    /// A command over the (schema, table) pair list. The <c>unnest()</c> pair keeps the whole
    /// list one parameterised round trip rather than a generated IN list, and keeps table names
    /// out of the SQL text.
    /// </summary>
    private static async Task<DbCommand> CommandAsync(
        DbContext context, string sql, IReadOnlyList<(string Schema, string Table)> tables, CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        AddArray(command, "schemas", [.. tables.Select(t => t.Schema)]);
        AddArray(command, "tables", [.. tables.Select(t => t.Table)]);
        if (context.Database.CurrentTransaction is { } transaction)
        {
            command.Transaction = transaction.GetDbTransaction();
        }
        return command;

        static void AddArray(DbCommand command, string name, string[] values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = values;
            command.Parameters.Add(parameter);
        }
    }
}
