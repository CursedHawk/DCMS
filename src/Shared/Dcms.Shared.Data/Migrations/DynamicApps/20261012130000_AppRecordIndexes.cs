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
        /// writes to apps.records while it reads the table, and a drop holds the table, so a call
        /// does at most <c>budget</c> of them, gives up on a lock after 500 ms and on the whole call
        /// after 15 s, and reports what is left. Reports carry index names and SQLSTATEs only: an
        /// error message can quote a tenant's value.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION apps.sync_record_indexes(wanted jsonb, budget int) RETURNS text[]
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp SET lock_timeout = '500ms' SET statement_timeout = '15s' AS $fn$
                DECLARE
                    uuid_pattern constant text := '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
                    item jsonb;
                    names text[] := '{}';
                    report text[] := '{}';
                    existing text;
                    done int := 0;
                    expr text;
                BEGIN
                    -- One caller at a time: admin-api replicas share the work.
                    IF NOT pg_try_advisory_xact_lock(hashtext('apps.sync_record_indexes')) THEN
                        RETURN ARRAY['busy'];
                    END IF;
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
                        IF done >= budget THEN
                            RETURN report || 'more'::text;
                        END IF;
                        BEGIN
                            EXECUTE format('DROP INDEX apps.%I', existing);
                            done := done + 1;
                            report := report || ('dropped ' || existing);
                        EXCEPTION WHEN others THEN
                            report := report || ('failed ' || existing || ' ' || SQLSTATE);
                        END;
                    END LOOP;

                    FOR item IN SELECT value FROM jsonb_array_elements(wanted) LOOP
                        CONTINUE WHEN EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'apps' AND indexname = item->>'name');
                        IF done >= budget THEN
                            report := report || 'more'::text;
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
            migrationBuilder.Sql("""
                DO $$
                DECLARE ix text;
                BEGIN
                    FOR ix IN SELECT indexname FROM pg_indexes WHERE schemaname = 'apps' AND indexname ~ '^ix_da_[0-9a-f]{24}$' LOOP
                        EXECUTE format('DROP INDEX apps.%I', ix);
                    END LOOP;
                END $$;
                DROP FUNCTION IF EXISTS apps.sync_record_indexes(jsonb, int);
                """);
        }
    }
}
