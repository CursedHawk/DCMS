#!/bin/sh
# The runtime connection for every service that reads tenant data (ADR 0015).
#
# The point of this role is one attribute: NOBYPASSRLS. The services connect as `dcms`
# today, and a table owner bypasses Row-Level Security -- so the tenant_isolation policies
# ADR 0005 shipped are, for the running platform, decoration. A non-owner is subject to
# them without FORCE ROW LEVEL SECURITY, which is why this is a role rather than an ALTER:
# forcing RLS would also constrain the migration jobs and RlsConfigurator itself.
#
# DML, never DDL. Migrations are the migrate jobs' work and they keep the owner connection.
# That split is deliberate beyond least privilege: a service that cannot ALTER a table
# cannot accidentally run one at startup against a half-rolled cluster.
#
# admin-api, content-api, site-host, media-worker and ai-gateway run as it (ADR 0015 phase 4).
#
# Re-applied on every deploy by the postgres-bootstrap job, so it is written idempotently.
set -e

PW="${APP_DB_PASSWORD:-dcms-app-dev}"

# Every schema holding a table with a TenantId, plus the three the five services also read and
# write through their own contexts today as the owner: dataprotection (the shared key ring --
# admin-api, content-api and ai-gateway mint and read keys there), edge (admin-api's certificate
# status and custom-upload endpoints) and platform (admin-api registers PlatformDbContext). None
# of those three carries a tenant column, so no policy applies to them; leaving them out would
# not have narrowed anything, it would have broken the service at the first cookie or
# certificate read. NOT identity, which is identity's alone, and NOT obs.
SCHEMAS="tenancy plugins cms media sites search analytics chat visitors ai forms audit social notifications dataprotection edge platform"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=app_pw="$PW" --set=owner="$POSTGRES_USER" <<'SQL'
-- \gexec rather than a DO block: psql interpolates :'app_pw' outside dollar-quotes and
-- would NOT inside a DO $$...$$ body.
SELECT format('CREATE ROLE dcms_app LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS', :'app_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_app')
\gexec

-- Always converge the password and the attributes. NOBYPASSRLS is the whole point of the
-- role, so it is re-asserted on every run rather than trusted to have stayed that way.
ALTER ROLE dcms_app LOGIN PASSWORD :'app_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
SQL

for schema in $SCHEMAS; do
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    --set=schema="$schema" --set=owner="$POSTGRES_USER" <<'SQL'
GRANT USAGE ON SCHEMA :"schema" TO dcms_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA :"schema" TO dcms_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA :"schema" TO dcms_app;

-- Tables the EF migrations create later are owned by `dcms`, so without this every new
-- table would be invisible to the role that has to read it -- and the symptom would be a
-- permission error on the one feature that shipped that table, long after the migration.
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA :"schema"
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO dcms_app;
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA :"schema"
  GRANT USAGE, SELECT ON SEQUENCES TO dcms_app;
SQL
done

echo "postgres-bootstrap: dcms_app role ready (NOBYPASSRLS; ADR 0015 phase 4 moves services onto it)"
