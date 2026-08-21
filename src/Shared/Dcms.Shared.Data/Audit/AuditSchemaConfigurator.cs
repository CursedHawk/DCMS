using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Data.Audit;

/// <summary>
/// Post-migration hardening and maintenance for the audit schema, applied on every startup.
/// Sibling of <see cref="Rls.RlsConfigurator"/> and idempotent for the same reason: EF creates
/// the tables, a configurator applies what EF cannot express, and both are safe to re-run.
///
/// <para><b>On the strength of the append-only controls here.</b> Seven of the eight services
/// connect as <c>dcms</c>, which the Postgres container creates as a cluster superuser — it can
/// disable the trigger and re-grant itself anything the REVOKE takes away. These controls are
/// therefore worth having against accidents and against any compromised non-superuser path,
/// and they are <b>not</b> the control against someone holding the application's credentials.
/// That control is the Vault-keyed HMAC chain plus off-box anchors. See ADR 0007; the standing
/// follow-up is to run the services under a non-superuser owner role.</para>
/// </summary>
public static class AuditSchemaConfigurator
{
    /// <summary>How many months of partitions to keep ahead of now.</summary>
    private const int MonthsAhead = 3;

    public static async Task ApplyAsync(DbContext context, ILogger logger, CancellationToken ct = default)
    {
        await EnsurePartitionsAsync(context, logger, ct);
        await EnsureChainIndexesAsync(context, logger, ct);
        await EnsureAppendOnlyAsync(context, logger, ct);
    }

    /// <summary>
    /// Creates this month's partition and the next few. Runs at every startup so a service that
    /// has been up across a month boundary — or one that was down when the maintenance worker
    /// would have run — still has somewhere to put today's records.
    /// </summary>
    public static async Task EnsurePartitionsAsync(DbContext context, ILogger logger, CancellationToken ct = default)
    {
        var month = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

        for (var i = 0; i <= MonthsAhead; i++)
        {
            var from = month.AddMonths(i);
            var to = from.AddMonths(1);
            var name = $"audit_events_{from:yyyy}m{from:MM}";

            var sql = $"""
                CREATE TABLE IF NOT EXISTS audit."{name}"
                    PARTITION OF audit.audit_events
                    FOR VALUES FROM ('{from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}')
                                 TO ('{to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}');
                """;

            try
            {
                await context.Database.ExecuteSqlRawAsync(sql, ct);
            }
            catch (Exception ex)
            {
                // The likely cause is rows for this month already sitting in the DEFAULT
                // partition, which blocks attaching a real one. Worth a warning rather than a
                // crash: writes still succeed into the default, so nothing is lost meanwhile.
                logger.LogWarning(ex, "Could not create audit partition {Partition}; records will fall to the default partition.", name);
            }
        }
    }

    /// <summary>
    /// Gives every partition its UNIQUE index on ("ChainKey", "Period", "Seq").
    ///
    /// <para>It cannot live on the parent: Postgres requires a unique index on a partitioned
    /// table to include the partition key, and adding OccurredAt to this one would defeat it —
    /// two rows could then claim the same Seq at different instants. Per-partition uniqueness
    /// is the real thing here, because Period is the month of OccurredAt and each partition is
    /// one month, so every row of a given (ChainKey, Period) lands in the same partition.</para>
    ///
    /// <para>Runs after <see cref="EnsurePartitionsAsync"/> and over <i>all</i> partitions, not
    /// just the ones just created, so a month created by an older build — or by the maintenance
    /// worker — is covered too. The write path serialises on the chain-head row lock; this index
    /// is the backstop behind it.</para>
    /// </summary>
    public static async Task EnsureChainIndexesAsync(DbContext context, ILogger logger, CancellationToken ct = default)
    {
        const string sql = """
            DO $$
            DECLARE part text;
            BEGIN
                FOR part IN
                    SELECT child.relname
                    FROM pg_inherits i
                    JOIN pg_class child ON child.oid = i.inhrelid
                    JOIN pg_class parent ON parent.oid = i.inhparent
                    JOIN pg_namespace n ON n.oid = parent.relnamespace
                    WHERE n.nspname = 'audit' AND parent.relname = 'audit_events'
                LOOP
                    EXECUTE format(
                        'CREATE UNIQUE INDEX IF NOT EXISTS %I ON audit.%I ("ChainKey", "Period", "Seq")',
                        'UX_' || part || '_chain', part);
                END LOOP;
            END $$;
            """;

        try
        {
            await context.Database.ExecuteSqlRawAsync(sql, ct);
        }
        catch (Exception ex)
        {
            // Duplicate (ChainKey, Period, Seq) rows would be the surprising cause, and that is
            // a chain problem to look at rather than a reason to refuse to start.
            logger.LogWarning(ex, "Could not ensure the audit chain unique indexes.");
        }
    }

    private static async Task EnsureAppendOnlyAsync(DbContext context, ILogger logger, CancellationToken ct)
    {
        const string sql = """
            CREATE OR REPLACE FUNCTION audit.deny_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'audit.audit_events is append-only (attempted % on %)', TG_OP, TG_TABLE_NAME;
            END $$;

            DROP TRIGGER IF EXISTS audit_events_append_only ON audit.audit_events;
            CREATE TRIGGER audit_events_append_only
                BEFORE UPDATE OR DELETE ON audit.audit_events
                FOR EACH ROW EXECUTE FUNCTION audit.deny_mutation();
            """;

        try
        {
            await context.Database.ExecuteSqlRawAsync(sql, ct);
            logger.LogInformation("Audit append-only trigger applied.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not apply the audit append-only trigger.");
        }

        // Best-effort, and only meaningful where the least-privilege roles exist at all.
        const string grantSql = """
            GRANT USAGE ON SCHEMA audit TO dcms_rls;
            GRANT SELECT ON audit.audit_events TO dcms_rls;
            """;
        try
        {
            await context.Database.ExecuteSqlRawAsync(grantSql, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Audit grants to dcms_rls skipped (role absent).");
        }
    }
}
