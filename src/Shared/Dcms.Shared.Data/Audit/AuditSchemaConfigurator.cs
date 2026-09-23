using Dcms.Shared.Data.Rls;
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
        await CreateMaintenanceFunctionsAsync(context, ct);
        await EnsurePartitionsAsync(context, logger, ct);
        await EnsureAppendOnlyAsync(context, logger, ct);
    }

    /// <summary>
    /// Creates this month's partition and the next few, protects each one, and gives every
    /// partition its chain index. Runs at every migration and every hour from the maintenance
    /// worker, so a service that has been up across a month boundary -- or one that was down
    /// when the worker would have run -- still has somewhere to put today's records.
    ///
    /// <para>Through <c>audit.ensure_partitions</c> rather than DDL of its own: the worker runs
    /// as <c>dcms_app</c> (ADR 0015), which may create no table. The function is owned by the
    /// migrating role and runs with its rights, and creating the next months' partitions is all
    /// it can be made to do.</para>
    /// </summary>
    public static async Task EnsurePartitionsAsync(DbContext context, ILogger logger, CancellationToken ct = default)
    {
        var failures = await context.Database
            .SqlQuery<string>($"SELECT unnest(audit.ensure_partitions({MonthsAhead})) AS \"Value\"")
            .ToListAsync(ct);

        foreach (var failure in failures)
        {
            // The likely cause is rows for a month already sitting in the DEFAULT partition,
            // which blocks attaching a real one. Worth a warning rather than a crash: writes
            // still succeed into the default, so nothing is lost meanwhile.
            logger.LogWarning("Audit partition maintenance: {Failure}", failure);
        }
    }

    /// <summary>
    /// The only DDL the audit log needs after migration, as functions the runtime role may call
    /// and nothing else. <c>SECURITY DEFINER</c> with a pinned <c>search_path</c>, and EXECUTE
    /// revoked from PUBLIC, which a new function otherwise grants to everyone.
    ///
    /// <para><c>ensure_partitions</c> is what <see cref="RlsConfigurator.ProtectAsync"/> and the
    /// chain index used to do per partition, in SQL. A partition is not covered by its parent's
    /// policy when a query names it directly, so it is protected as it is created;
    /// <c>RlsCoverageTests</c> holds both copies of the policy to the same catalogue check.</para>
    ///
    /// <para><c>drop_sealed_partition</c> is <see cref="AuditRetention"/>'s drop with its safety
    /// property moved into the database: it refuses the current or a future month, and any
    /// month holding a row whose chain has no anchor. It reads the rows rather than
    /// <c>chain_heads</c> on purpose -- the app role can delete a head, and a check over heads
    /// would then pass for a month nobody sealed.</para>
    /// </summary>
    private static async Task CreateMaintenanceFunctionsAsync(DbContext context, CancellationToken ct)
    {
        // ponytail: an anchor's HMAC cannot be checked in SQL, so a forged anchor row satisfies
        // drop_sealed_partition. Verification against the Vault key still exposes the forgery,
        // but the month is gone. Moving drops into the migrate job closes that, at the cost of
        // retention only running when something deploys.
        const string sql = """
            CREATE OR REPLACE FUNCTION audit.ensure_partitions(months_ahead int) RETURNS text[]
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
                m date := date_trunc('month', now() AT TIME ZONE 'UTC')::date;
                part text;
                failures text[] := ARRAY[]::text[];
            BEGIN
                FOR i IN 0..months_ahead LOOP
                    part := 'audit_events_' || to_char(m, 'YYYY') || 'm' || to_char(m, 'MM');
                    BEGIN
                        EXECUTE format(
                            'CREATE TABLE IF NOT EXISTS audit.%I PARTITION OF audit.audit_events FOR VALUES FROM (%L) TO (%L)',
                            part, m, (m + interval '1 month')::date);
                        EXECUTE format('ALTER TABLE audit.%I ENABLE ROW LEVEL SECURITY', part);
                        EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON audit.%I', part);
                        EXECUTE format(
                            'CREATE POLICY tenant_isolation ON audit.%I '
                            'USING ("TenantId" = nullif(current_setting(''app.tenant_id'', true), '''')::uuid) '
                            'WITH CHECK ("TenantId" = nullif(current_setting(''app.tenant_id'', true), '''')::uuid)', part);
                        EXECUTE format('DROP POLICY IF EXISTS platform_scope ON audit.%I', part);
                        EXECUTE format(
                            'CREATE POLICY platform_scope ON audit.%I '
                            'USING (current_setting(''app.scope'', true) = ''platform'') '
                            'WITH CHECK (current_setting(''app.scope'', true) = ''platform'')', part);
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_rls') THEN
                            EXECUTE format('GRANT SELECT ON audit.%I TO dcms_rls', part);
                        END IF;
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_app') THEN
                            EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON audit.%I TO dcms_app', part);
                        END IF;
                    EXCEPTION WHEN others THEN
                        failures := failures || format('could not create %s (%s); records will fall to the default partition', part, SQLERRM);
                    END;
                    m := (m + interval '1 month')::date;
                END LOOP;

                -- Per partition, never on the parent: a unique index on a partitioned table must
                -- include the partition key, and adding OccurredAt would let two rows claim one
                -- Seq. Period is the month of OccurredAt, so per-partition uniqueness is the real
                -- thing. Over ALL partitions, so one made by an older build is covered too.
                FOR part IN
                    SELECT child.relname
                    FROM pg_inherits i
                    JOIN pg_class child ON child.oid = i.inhrelid
                    JOIN pg_class parent ON parent.oid = i.inhparent
                    JOIN pg_namespace n ON n.oid = parent.relnamespace
                    WHERE n.nspname = 'audit' AND parent.relname = 'audit_events'
                LOOP
                    BEGIN
                        EXECUTE format(
                            'CREATE UNIQUE INDEX IF NOT EXISTS %I ON audit.%I ("ChainKey", "Period", "Seq")',
                            'UX_' || part || '_chain', part);
                    EXCEPTION WHEN others THEN
                        -- Duplicate (ChainKey, Period, Seq) rows would be the surprising cause:
                        -- a chain problem to look at, not a reason to refuse to start.
                        failures := failures || format('could not index %s (%s)', part, SQLERRM);
                    END;
                END LOOP;

                RETURN failures;
            END $fn$;

            CREATE OR REPLACE FUNCTION audit.drop_sealed_partition(p_period date) RETURNS boolean
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
                part text := 'audit_events_' || to_char(p_period, 'YYYY') || 'm' || to_char(p_period, 'MM');
                unsealed boolean;
            BEGIN
                IF p_period <> date_trunc('month', p_period)::date
                   OR p_period >= date_trunc('month', now() AT TIME ZONE 'UTC')::date THEN
                    RAISE EXCEPTION 'audit.%: not a finished month', part;
                END IF;
                IF to_regclass(format('audit.%I', part)) IS NULL THEN
                    RETURN false;
                END IF;

                EXECUTE format(
                    'SELECT EXISTS (SELECT 1 FROM audit.%I e WHERE NOT EXISTS ('
                    'SELECT 1 FROM audit.chain_anchors a WHERE a."ChainKey" = e."ChainKey" AND a."Period" = %L))',
                    part, p_period) INTO unsealed;
                IF unsealed THEN
                    RAISE EXCEPTION 'audit.%: holds records of an unsealed chain', part;
                END IF;

                -- Detach first so a reader mid-query against the parent sees a clean partition set.
                EXECUTE format('ALTER TABLE audit.audit_events DETACH PARTITION audit.%I', part);
                EXECUTE format('DROP TABLE audit.%I', part);
                RETURN true;
            END $fn$;

            REVOKE ALL ON FUNCTION audit.ensure_partitions(int) FROM PUBLIC;
            REVOKE ALL ON FUNCTION audit.drop_sealed_partition(date) FROM PUBLIC;
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_app') THEN
                    GRANT EXECUTE ON FUNCTION audit.ensure_partitions(int) TO dcms_app;
                    GRANT EXECUTE ON FUNCTION audit.drop_sealed_partition(date) TO dcms_app;
                END IF;
            END $$;
            """;

        await context.Database.ExecuteSqlRawAsync(sql, ct);
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
