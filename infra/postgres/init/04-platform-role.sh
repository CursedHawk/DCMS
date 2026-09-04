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
# NO PASSWORD IS SET HERE, and that is the point.
#
# This runs in a bare `postgres` image with no Vault client and no way to get one — it has
# neither curl nor wget, only perl — so any password it could set would have to arrive through
# the environment, which is the file we are trying to stop keeping secrets in. So this script
# creates the role and its grants and stops there: a role with no password cannot log in, and
# the deploy is ordered so that nothing tries to.
#
# The password is set immediately afterwards by the `migrate` job, which runs the admin-api
# image -- a service that already has the Vault config provider, already connects as the schema
# owner, and is therefore already strictly more privileged than the role it is configuring.
# See PlatformRoleConfigurator.
#
# Between the two, the role exists and cannot be used from anywhere that matters: the official
# image's pg_hba ends with `host all all all scram-sha-256`, and scram against a role with no
# stored password fails. (The `trust` lines above it cover the unix socket and 127.0.0.1 inside
# the container, which are already unauthenticated for every role including the owner -- so
# they are not a window this opens.)
#
# Runs on FIRST cluster init and again on every deploy via the `postgres-bootstrap` job, which
# re-applies infra/postgres/init/* against the running cluster. Every statement is idempotent.
set -e

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=owner="$POSTGRES_USER" <<'SQL'
-- \gexec rather than a DO block, matching 02-service-roles.sh and 03-observability-role.sh.
SELECT 'CREATE ROLE dcms_platform LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS'
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_platform')
\gexec

-- Converge the attributes, never the password: this script has none to converge, and an
-- ALTER without a PASSWORD clause leaves the existing one untouched.
ALTER ROLE dcms_platform LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

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

echo "platform-api DB role provisioned (password is set by the migrate job from Vault)."
