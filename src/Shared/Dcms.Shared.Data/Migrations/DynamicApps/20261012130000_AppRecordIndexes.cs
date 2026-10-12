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
        /// DDL it can ask for, and only in this shape: names, table and field ids are checked,
        /// values are quoted, and every index is a partial index of apps.records.
        /// </summary>
        /// <remarks>
        /// Not CONCURRENTLY: a function runs in a transaction, where that is refused. A build holds
        /// writes to apps.records while it reads the table, so a call makes at most <c>budget</c>,
        /// gives up on a lock after 500 ms, and reports what is left for the next call.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION apps.sync_record_indexes(wanted jsonb, budget int) RETURNS text[]
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp SET lock_timeout = '500ms' AS $fn$
                DECLARE
                    uuid_pattern constant text := '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
                    item jsonb;
                    names text[] := '{}';
                    report text[] := '{}';
                    existing text;
                    made int := 0;
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
                        BEGIN
                            EXECUTE format('DROP INDEX apps.%I', existing);
                            report := report || ('dropped ' || existing);
                        EXCEPTION WHEN others THEN
                            report := report || ('failed ' || existing || ': ' || SQLERRM);
                        END;
                    END LOOP;

                    FOR item IN SELECT value FROM jsonb_array_elements(wanted) LOOP
                        CONTINUE WHEN EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'apps' AND indexname = item->>'name');
                        IF made >= budget THEN
                            report := report || 'more'::text;
                            EXIT;
                        END IF;
                        -- Character for character what the query compiler writes (RecordIndexes.KeySql).
                        expr := CASE item->>'kind'
                            WHEN 'number' THEN format('(("Data" ->> %L))::numeric', item->>'key')
                            ELSE format('left(("Data" ->> %L), 200)', item->>'key') END;
                        BEGIN
                            EXECUTE format('CREATE INDEX %I ON apps.records ((%s) %s NULLS LAST, "Id") WHERE "TableId" = %L::uuid',
                                item->>'name', expr, CASE WHEN (item->>'descending')::boolean THEN 'DESC' ELSE 'ASC' END, item->>'table');
                            made := made + 1;
                            report := report || ('created ' || (item->>'name'));
                        EXCEPTION WHEN others THEN
                            report := report || ('failed ' || (item->>'name') || ': ' || SQLERRM);
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
