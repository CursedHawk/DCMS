using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Folds ADR 0026's environment tenants into their workspace as environments of it (ADR 0028).
/// Runs at startup after every migration, and does anything only while <c>tenancy.tenants</c>
/// still has the <c>WorkspaceId</c> column, which it drops at the end: idempotent, and a no-op
/// on every database converted before.
///
/// <para>For each environment tenant E of workspace W, in one transaction: E's row in
/// <c>tenancy.environments</c> becomes an environment of W under the same id, so everything keyed
/// by E -- realms in identity, Redis keys, edge rows, plugin files -- stays valid. Every table
/// with an environment column is re-keyed to (W, E); every other tenant table moves to W, except
/// what was E's copy of the workspace's: its mirrored memberships (role assignments move to W's
/// membership of the same person), invitations, usage, AI settings W has its own of, and
/// notifications W already holds; analytics rollups of the same key are summed, and job dedupe
/// keys W already uses are cleared. Audit rows stay where they were written: the log is
/// append-only, and its chain is per tenant.</para>
///
/// <para>Media rows keep their object keys, so what E uploaded is served from where it is.</para>
/// </summary>
public static class EnvironmentConversion
{
    // Never re-keyed: append-only (audit), or keyed by environment already (edge). Only the
    // schemas of the platform's own tenant tables are touched at all: identity shares the
    // database, and its realms are keyed by the environment, which is the point.
    private static readonly string[] SkippedSchemas = ["audit", "edge"];

    // E's copy of what is the workspace's: deleted, not moved.
    private static readonly (string Schema, string Table)[] Dropped =
        [("tenancy", "invitations"), ("tenancy", "tenant_usage"), ("ai", "tenant_ai_settings")];

    public static async Task ApplyAsync(TenancyDbContext db, ILogger logger, CancellationToken ct)
    {
        // rls: platform -- it re-keys every tenant's rows across every schema, naming each one.
        using var platform = RlsScope.Platform();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            if (!await ScalarAsync<bool>(db, null, """
                    SELECT EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'tenancy' AND table_name = 'tenants' AND column_name = 'WorkspaceId')
                    """, ct))
            {
                return;
            }

            var tables = new List<(string Schema, string Table, bool Environment)>();
            await using (var cmd = Command(db, null, """
                    SELECT n.nspname, c.relname,
                           EXISTS (SELECT 1 FROM pg_attribute a2 WHERE a2.attrelid = c.oid AND a2.attname = 'EnvironmentId' AND NOT a2.attisdropped)
                    FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'TenantId' AND NOT a.attisdropped
                    WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
                    ORDER BY n.nspname, c.relname
                    """))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    tables.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
                }
            }
            var schemas = RlsConfigurator.TenantTables.Select(t => t.Schema).ToHashSet(StringComparer.Ordinal);
            tables.RemoveAll(t => !schemas.Contains(t.Schema) || SkippedSchemas.Contains(t.Schema)
                                  || (t.Schema, t.Table) is ("tenancy", "environments") or ("tenancy", "tenant_memberships")
                                  || Dropped.Contains((t.Schema, t.Table)));

            var rows = new List<(Guid Id, Guid Workspace, string? Name, int Order, Guid[] PromotesFrom)>();
            await using (var cmd = Command(db, null, """
                    SELECT "Id"::uuid, "WorkspaceId", "EnvironmentName", "EnvironmentOrder", "PromotesFrom"
                    FROM tenancy.tenants WHERE "WorkspaceId" IS NOT NULL
                    """))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetInt32(3), reader.GetFieldValue<Guid[]>(4)));
                }
            }

            foreach (var (id, workspace, name, order, promotesFrom) in rows.OrderBy(r => r.Id == r.Workspace))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                // Production's pipeline settings, or the environment row taken over by the workspace.
                await ExecAsync(db, tx, """
                    INSERT INTO tenancy.environments ("Id", "TenantId", "Name", "Order", "Purpose", "PromotesFrom", "MergesOwnChangesOnly", "DeleteWhenMerged", "CreatedAt")
                    VALUES (@e, @w, @name, @order, 'Stage', @from, false, false, now())
                    ON CONFLICT ("Id") DO UPDATE
                    SET "TenantId" = @w, "Name" = @name, "Order" = @order, "PromotesFrom" = @from
                    """, ct, ("w", workspace), ("e", id),
                    ("name", id == workspace ? TenantEnvironment.Production : name ?? $"environment-{id:N}"[..20]),
                    ("order", order), ("from", promotesFrom));
                if (id == workspace)
                {
                    await tx.CommitAsync(ct);
                    continue;
                }

                // Role assignments follow the person to the workspace's own membership.
                await ExecAsync(db, tx, """
                    UPDATE tenancy.member_roles mr SET "MembershipId" = w."Id"
                    FROM tenancy.tenant_memberships e
                    JOIN tenancy.tenant_memberships w ON w."TenantId" = @w AND w."UserId" = e."UserId"
                    WHERE mr."MembershipId" = e."Id" AND e."TenantId" = @e
                    """, ct, ("w", workspace), ("e", id));
                await ExecAsync(db, tx, """
                    DELETE FROM tenancy.member_roles WHERE "MembershipId" IN (SELECT "Id" FROM tenancy.tenant_memberships WHERE "TenantId" = @e);
                    DELETE FROM tenancy.tenant_memberships WHERE "TenantId" = @e;
                    DELETE FROM ai.user_ai_settings x WHERE x."TenantId" = @e
                        AND EXISTS (SELECT 1 FROM ai.user_ai_settings w WHERE w."TenantId" = @w AND w."UserId" = x."UserId");
                    DELETE FROM notifications.recipients WHERE "NotificationId" IN (
                        SELECT x."Id" FROM notifications.notifications x WHERE x."TenantId" = @e
                        AND EXISTS (SELECT 1 FROM notifications.notifications w WHERE w."TenantId" = @w AND w."DedupeKey" = x."DedupeKey"));
                    DELETE FROM notifications.notifications x WHERE x."TenantId" = @e
                        AND EXISTS (SELECT 1 FROM notifications.notifications w WHERE w."TenantId" = @w AND w."DedupeKey" = x."DedupeKey");
                    -- Rollups of the same day, site, type and path are one row per tenant: summed.
                    UPDATE analytics.daily_rollups w SET "Count" = w."Count" + x."Count"
                    FROM analytics.daily_rollups x
                    WHERE w."TenantId" = @w AND x."TenantId" = @e AND w."Day" = x."Day" AND w."SiteId" = x."SiteId"
                        AND w."Type" = x."Type" AND w."Path" = x."Path";
                    DELETE FROM analytics.daily_rollups x WHERE x."TenantId" = @e
                        AND EXISTS (SELECT 1 FROM analytics.daily_rollups w WHERE w."TenantId" = @w AND w."Day" = x."Day"
                            AND w."SiteId" = x."SiteId" AND w."Type" = x."Type" AND w."Path" = x."Path");
                    -- A dedupe key only stops the same job being asked for twice; E's are history.
                    UPDATE jobs.jobs x SET "DedupeKey" = NULL WHERE x."TenantId" = @e AND x."DedupeKey" IS NOT NULL
                        AND EXISTS (SELECT 1 FROM jobs.jobs w WHERE w."TenantId" = @w AND w."DedupeKey" = x."DedupeKey");
                    """, ct, ("w", workspace), ("e", id));
                foreach (var (schema, table) in Dropped)
                {
                    await ExecAsync(db, tx, $"""DELETE FROM "{schema}"."{table}" WHERE "TenantId" = @e""", ct, ("e", id));
                }

                var moved = 0;
                foreach (var (schema, table, environment) in tables)
                {
                    moved += await ExecAsync(db, tx, environment
                            ? $"""UPDATE "{schema}"."{table}" SET "TenantId" = @w, "EnvironmentId" = @e WHERE "TenantId" = @e"""
                            : $"""UPDATE "{schema}"."{table}" SET "TenantId" = @w WHERE "TenantId" = @e""",
                        ct, ("w", workspace), ("e", id));
                }
                await ExecAsync(db, tx, """DELETE FROM tenancy.tenants WHERE "Id" = @id""", ct, ("id", id.ToString()));
                await tx.CommitAsync(ct);
                logger.LogWarning("Environment tenant {EnvironmentId} folded into workspace {WorkspaceId} as environment {Name}: {Rows} rows re-keyed.",
                    id, workspace, name, moved);
            }

            // CASCADE: the reporting views read these columns, and are rebuilt right after this.
            await ExecAsync(db, null, """
                ALTER TABLE tenancy.tenants
                    DROP COLUMN IF EXISTS "WorkspaceId" CASCADE,
                    DROP COLUMN IF EXISTS "EnvironmentName" CASCADE,
                    DROP COLUMN IF EXISTS "EnvironmentOrder" CASCADE,
                    DROP COLUMN IF EXISTS "PromotesFrom" CASCADE
                """, ct);
            logger.LogWarning("Environments are a dimension of their tenant now: {Count} environment tenants converted.",
                rows.Count(r => r.Id != r.Workspace));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static NpgsqlCommand Command(TenancyDbContext db, NpgsqlTransaction? tx, string sql)
    {
        var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(), tx)
        {
            // Re-keying a workspace touches every row it has, as a migration does.
            CommandTimeout = (int)DcmsMigrationRunner.MigrationCommandTimeout.TotalSeconds,
        };
        TenantGucInterceptor.Prepare(db, cmd);
        return cmd;
    }

    private static async Task<int> ExecAsync(
        TenancyDbContext db, NpgsqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = Command(db, tx, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(TenancyDbContext db, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = Command(db, tx, sql);
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
