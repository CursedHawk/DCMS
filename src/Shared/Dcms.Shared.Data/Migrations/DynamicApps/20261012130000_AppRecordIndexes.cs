using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.DynamicApps
{
    /// <inheritdoc />
    public partial class AppRecordIndexes : Migration
    {
        /// <summary>
        /// <c>apps.sync_record_indexes</c>: the indexes behind sortable and filterable fields
        /// (RecordIndexes in the Dynamic Apps plugin), made and dropped with the owner's rights.
        /// admin-api runs as dcms_app, which may create no index (ADR 0015), so this is the only
        /// DDL it can ask for, and only in this shape: names and ids are checked, values are quoted,
        /// and every index is a partial index of apps.records over ONE app's table, by tenant,
        /// instance and table: table ids are chosen by authors, so the table id alone could name
        /// another tenant's rows.
        /// </summary>
        /// <remarks>
        /// Not CONCURRENTLY: a function runs in a transaction, where that is refused. A build holds
        /// writes to apps.records while it reads the table, and a drop holds the table, so the time
        /// spent on them is rationed for the whole platform in <c>apps.record_index_sync</c>: at most
        /// 2 minutes an hour, and after each call a pause of 9 times what it took (at most a tenth of
        /// the time). A call does at most <c>budget</c> operations, drops at most half of them, and
        /// gives up on a lock after 500 ms; the caller bounds the call with its own
        /// statement_timeout (one set here would not apply to the statement already running) and
        /// charges a call it cancelled through <c>apps.record_index_charge</c>, whose rollback
        /// lost the accounting. Reports carry index names and SQLSTATEs only: an error message can
        /// quote a tenant's value.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- One row: how much index DDL time the platform has spent this hour, and when it may next.
                CREATE TABLE IF NOT EXISTS apps.record_index_sync (
                    id int PRIMARY KEY DEFAULT 1 CHECK (id = 1),
                    window_start timestamptz NOT NULL DEFAULT now(),
                    spent_ms bigint NOT NULL DEFAULT 0,
                    next_at timestamptz NOT NULL DEFAULT now());
                INSERT INTO apps.record_index_sync (id) VALUES (1) ON CONFLICT DO NOTHING;
                REVOKE ALL ON apps.record_index_sync FROM PUBLIC;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_app') THEN
                        REVOKE ALL ON apps.record_index_sync FROM dcms_app;
                    END IF;
                END $$;

                -- Can only make the ration stricter, so the runtime role may call it.
                CREATE OR REPLACE FUNCTION apps.record_index_charge(ms int) RETURNS void
                    LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
                    UPDATE apps.record_index_sync
                    SET spent_ms = spent_ms + greatest(ms, 0),
                        next_at = greatest(next_at, now() + make_interval(secs => greatest(ms, 0) * 9 / 1000.0))
                    WHERE id = 1;
                $fn$;
                REVOKE ALL ON FUNCTION apps.record_index_charge(int) FROM PUBLIC;

                CREATE OR REPLACE FUNCTION apps.sync_record_indexes(wanted jsonb, budget int) RETURNS text[]
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp SET lock_timeout = '500ms' AS $fn$
                DECLARE
                    uuid_pattern constant text := '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
                    item jsonb;
                    names text[] := '{}';
                    report text[] := '{}';
                    existing text;
                    done int := 0;
                    dropped int := 0;
                    ration apps.record_index_sync;
                    started timestamptz;
                    spent bigint;
                    expr text;
                BEGIN
                    -- One caller at a time: admin-api replicas share the work.
                    IF NOT pg_try_advisory_xact_lock(hashtext('apps.sync_record_indexes')) THEN
                        RETURN ARRAY['busy'];
                    END IF;
                    UPDATE apps.record_index_sync SET window_start = now(), spent_ms = 0
                    WHERE id = 1 AND window_start < now() - interval '1 hour';
                    SELECT * INTO ration FROM apps.record_index_sync WHERE id = 1;
                    IF now() < ration.next_at OR ration.spent_ms >= 120000 THEN
                        RETURN ARRAY['wait'];
                    END IF;
                    started := clock_timestamp();
                    IF jsonb_typeof(wanted) <> 'array' THEN
                        RAISE EXCEPTION 'wanted must be an array';
                    END IF;
                    FOR item IN SELECT value FROM jsonb_array_elements(wanted) LOOP
                        IF coalesce(item->>'name', '') !~ '^ix_da_[0-9a-f]{24}$'
                           OR coalesce(item->>'tenant', '') !~ uuid_pattern
                           OR coalesce(item->>'instance', '') !~ uuid_pattern
                           OR coalesce(item->>'table', '') !~ uuid_pattern
                           OR coalesce(item->>'key', '') !~ uuid_pattern
                           OR coalesce(item->>'kind', '') NOT IN ('text', 'number')
                           OR coalesce(item->>'descending', '') NOT IN ('true', 'false') THEN
                            RAISE EXCEPTION 'not an index this function makes: %', item;
                        END IF;
                        names := names || (item->>'name');
                    END LOOP;

                    FOR existing IN
                        SELECT indexname FROM pg_indexes
                        WHERE schemaname = 'apps' AND tablename = 'records' AND indexname ~ '^ix_da_[0-9a-f]{24}$'
                          AND NOT (indexname = ANY (names))
                    LOOP
                        -- Half the budget at most: drops must not crowd out other tenants' builds.
                        IF dropped >= greatest(budget / 2, 1) THEN
                            report := report || 'more'::text;
                            EXIT;
                        END IF;
                        BEGIN
                            EXECUTE format('DROP INDEX apps.%I', existing);
                            done := done + 1;
                            dropped := dropped + 1;
                            report := report || ('dropped ' || existing);
                        EXCEPTION WHEN others THEN
                            report := report || ('failed ' || existing || ' ' || SQLSTATE);
                        END;
                    END LOOP;

                    FOR item IN SELECT value FROM jsonb_array_elements(wanted) LOOP
                        CONTINUE WHEN EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'apps' AND indexname = item->>'name');
                        IF done >= budget THEN
                            IF NOT ('more' = ANY (report)) THEN
                                report := report || 'more'::text;
                            END IF;
                            EXIT;
                        END IF;
                        -- Character for character what the query compiler writes (RecordIndexes.KeySql).
                        expr := CASE item->>'kind'
                            WHEN 'number' THEN format('(("Data" ->> %L))::numeric', item->>'key')
                            ELSE format('left(("Data" ->> %L), 200)', item->>'key') END;
                        BEGIN
                            EXECUTE format('CREATE INDEX %I ON apps.records ((%s) %s NULLS LAST, "Id") '
                                    'WHERE "TenantId" = %L::uuid AND "InstanceId" = %L::uuid AND "TableId" = %L::uuid',
                                item->>'name', expr, CASE WHEN (item->>'descending')::boolean THEN 'DESC' ELSE 'ASC' END,
                                item->>'tenant', item->>'instance', item->>'table');
                            done := done + 1;
                            report := report || ('created ' || (item->>'name'));
                        EXCEPTION WHEN others THEN
                            report := report || ('failed ' || (item->>'name') || ' ' || SQLSTATE);
                        END;
                    END LOOP;
                    IF done > 0 THEN
                        spent := (extract(epoch FROM clock_timestamp() - started) * 1000)::bigint;
                        UPDATE apps.record_index_sync
                        SET spent_ms = spent_ms + spent, next_at = now() + make_interval(secs => greatest(2, spent * 9 / 1000.0))
                        WHERE id = 1;
                    END IF;
                    RETURN report;
                END;
                $fn$;

                REVOKE ALL ON FUNCTION apps.sync_record_indexes(jsonb, int) FROM PUBLIC;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_app') THEN
                        GRANT EXECUTE ON FUNCTION apps.sync_record_indexes(jsonb, int) TO dcms_app;
                        GRANT EXECUTE ON FUNCTION apps.record_index_charge(int) TO dcms_app;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE ix text;
                BEGIN
                    FOR ix IN SELECT indexname FROM pg_indexes WHERE schemaname = 'apps' AND indexname ~ '^ix_da_[0-9a-f]{24}$' LOOP
                        EXECUTE format('DROP INDEX apps.%I', ix);
                    END LOOP;
                END $$;
                DROP FUNCTION IF EXISTS apps.sync_record_indexes(jsonb, int);
                DROP FUNCTION IF EXISTS apps.record_index_charge(int);
                DROP TABLE IF EXISTS apps.record_index_sync;
                """);
        }
    }
}
