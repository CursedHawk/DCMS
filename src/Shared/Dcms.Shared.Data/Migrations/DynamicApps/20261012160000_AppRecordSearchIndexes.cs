using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dcms.Shared.Data.Migrations.DynamicApps
{
    /// <inheritdoc />
    public partial class AppRecordSearchIndexes : Migration
    {
        /// <summary>
        /// Trigram indexes for searchable fields: <c>pg_trgm</c> (a trusted extension, so the database
        /// owner may create it), and <c>apps.sync_record_indexes</c> again with a third kind,
        /// <c>search</c>: a GIN index of the lowered value's trigrams, which serves the
        /// <c>lower(x) LIKE '%term%'</c> search the query compiler writes. Everything else about the
        /// function — checks, scope, ration, reports — is as AppRecordIndexes made it.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS pg_trgm;

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
                    trgm text;
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
                    SELECT n.nspname INTO trgm FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'pg_trgm';
                    IF jsonb_typeof(wanted) <> 'array' THEN
                        RAISE EXCEPTION 'wanted must be an array';
                    END IF;
                    FOR item IN SELECT value FROM jsonb_array_elements(wanted) LOOP
                        IF coalesce(item->>'name', '') !~ '^ix_da_[0-9a-f]{24}$'
                           OR coalesce(item->>'tenant', '') !~ uuid_pattern
                           OR coalesce(item->>'instance', '') !~ uuid_pattern
                           OR coalesce(item->>'table', '') !~ uuid_pattern
                           OR coalesce(item->>'key', '') !~ uuid_pattern
                           OR coalesce(item->>'kind', '') NOT IN ('text', 'number', 'search')
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
                            WHEN 'search' THEN format('lower(("Data" ->> %L))', item->>'key')
                            ELSE format('left(("Data" ->> %L), 200)', item->>'key') END;
                        BEGIN
                            IF item->>'kind' = 'search' THEN
                                -- Trigrams of the lowered value: what `lower(x) LIKE '%term%'` searches.
                                IF trgm IS NULL THEN
                                    RAISE EXCEPTION USING ERRCODE = '58P01';
                                END IF;
                                EXECUTE format('CREATE INDEX %I ON apps.records USING gin ((%s) %I.gin_trgm_ops) '
                                        'WHERE "TenantId" = %L::uuid AND "InstanceId" = %L::uuid AND "TableId" = %L::uuid',
                                    item->>'name', expr, trgm, item->>'tenant', item->>'instance', item->>'table');
                            ELSE
                                EXECUTE format('CREATE INDEX %I ON apps.records ((%s) %s NULLS LAST, "Id") '
                                        'WHERE "TenantId" = %L::uuid AND "InstanceId" = %L::uuid AND "TableId" = %L::uuid',
                                    item->>'name', expr, CASE WHEN (item->>'descending')::boolean THEN 'DESC' ELSE 'ASC' END,
                                    item->>'tenant', item->>'instance', item->>'table');
                            END IF;
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
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The search indexes go on the next sync (they are no longer wanted); the extension stays,
            // as other schemas may use it.
        }
    }
}
