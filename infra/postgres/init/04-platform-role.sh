#!/bin/sh
# Least-privilege DB role for platform-api (the platform console at platform.highgeek.eu).
#
# This service is the one holding the platform's delete buttons — Loki purges, Prometheus
# series deletion, container log truncation — so the interesting question is not what it can
# reach but what it cannot. The answer is: almost everything.
#
# It needs exactly two things from Postgres:
#
#   1. SELECT on the `obs` reporting views, for every cross-tenant page in the console.
#   2. Read/write on `platform.role_permissions`, which is which global role holds which
#      console permission.
#
# It needs nothing else, because it owns nothing else. Users are reached over HTTP from
# identity (which owns them and holds UserManager); tenants over HTTP from admin-api (which
# owns the tenancy schema, per ADR 0003). So this role has no grant on identity, tenancy, cms,
# media, sites or any tenant schema, and a compromised platform-api cannot read a password
# hash, a token, or one tenant's content.
#
# NOBYPASSRLS, unlike dcms_sitebuilder: this role never queries a tenant table, so there is no
# cross-tenant read for RLS to get in the way of. The `obs` views are owned by the superuser
# and execute with its rights, which is what lets them report across tenants without granting
# this role a single base table.
#
# Not default_transaction_read_only, unlike dcms_grafana: this role does write, to exactly one
# table. The `platform` schema grants below are the whole of its write surface.
#
# Runs on FIRST cluster init and again on every deploy via the `postgres-bootstrap` job, which
# re-applies infra/postgres/init/* against the running cluster. Every statement is idempotent.
set -e

PW="${PLATFORM_DB_PASSWORD:-dcms-platform-dev}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=pf_pw="$PW" --set=owner="$POSTGRES_USER" <<'SQL'
-- \gexec rather than a DO block: psql interpolates :'pf_pw' in a plain statement but not
-- inside dollar-quoted body text. Same pattern as 02-service-roles.sh and 03-observability-role.sh.
SELECT format('CREATE ROLE dcms_platform LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS', :'pf_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_platform')
\gexec

-- Always converge the password/attributes for an existing role.
ALTER ROLE dcms_platform LOGIN PASSWORD :'pf_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

-- A console page that plans badly must not hold a connection indefinitely. The console
-- refreshes on a timer like a dashboard does, so a slow query fails repeatedly rather than
-- once, and without these it accumulates.
ALTER ROLE dcms_platform SET statement_timeout = '30s';
ALTER ROLE dcms_platform SET idle_in_transaction_session_timeout = '60s';

-- 1. The reporting views. Created and re-granted on every admin-api start by
--    ObservabilityViewConfigurator; this grant is for a fresh cluster where the schema may
--    exist before any view does.
CREATE SCHEMA IF NOT EXISTS obs;
GRANT USAGE ON SCHEMA obs TO dcms_platform;

-- 2. The console's own table. The schema and table are created by the `migrate` job as the
--    owner (PlatformDbContext); this only hands out access to them. CREATE SCHEMA here so the
--    grants below are valid on a cluster that has not run the migration yet.
CREATE SCHEMA IF NOT EXISTS platform AUTHORIZATION :"owner";
GRANT USAGE ON SCHEMA platform TO dcms_platform;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA platform TO dcms_platform;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA platform TO dcms_platform;

-- Tables the migrate job creates LATER (owned by the dcms owner) become accessible
-- automatically, which is what makes this script safe to run before the migration.
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA platform
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO dcms_platform;
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA platform
  GRANT USAGE, SELECT ON SEQUENCES TO dcms_platform;
SQL

echo "platform-api DB role provisioned."
