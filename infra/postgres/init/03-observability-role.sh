#!/bin/sh
# Read-only reporting role for Grafana.
#
# Metrics cannot answer "what is this tenant doing" or "how many users signed up in March"
# without unbounded label cardinality, and Postgres already holds those answers. So Grafana
# gets a SQL datasource — but not on the terms the application connects.
#
# This role's entire world is the `obs` schema: SELECT on a handful of views and USAGE on
# nothing else. It cannot read identity.asp_net_users, cannot read a password hash, cannot
# read a tenant's content. That matters more than it might look, because Grafana always lets
# an editor type raw SQL into a panel — there is no setting that prevents it. So the control
# has to live in the database, where it does.
#
# Not the `dcms` superuser that seven services use, for the obvious reason.
#
# Not `dcms_rls` either, for a less obvious one: RLS policies key on the `app.tenant_id`
# setting, and a connection that never sets it sees zero rows in every tenant table. That is
# the correct behaviour for per-tenant access and exactly wrong for platform-wide reporting.
# dcms_rls is the right role for a future per-tenant Grafana org; it is not this.
#
# The views themselves are created and re-granted on every admin-api startup by
# ObservabilityViewConfigurator, because infra/postgres/init/* runs only on a fresh cluster
# and vps1 was provisioned long ago. This script exists for fresh clusters; on an existing
# one, run the CREATE ROLE below by hand once (see infra/observability/README.md).
set -e

PW="${GRAFANA_DB_PASSWORD:-dcms-grafana-dev}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=gf_pw="$PW" <<'SQL'
-- \gexec rather than a DO block: psql interpolates :'gf_pw' in a plain statement but not
-- inside dollar-quoted body text. Same pattern as 02-service-roles.sh.
SELECT format('CREATE ROLE dcms_grafana LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS', :'gf_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_grafana')
\gexec

ALTER ROLE dcms_grafana LOGIN PASSWORD :'gf_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

-- A reporting query that plans badly must not be able to hold a lock or a connection
-- indefinitely. Grafana panels refresh on a timer, so a slow query does not fail once — it
-- fails repeatedly, and without these it accumulates.
ALTER ROLE dcms_grafana SET statement_timeout = '30s';
ALTER ROLE dcms_grafana SET idle_in_transaction_session_timeout = '60s';
ALTER ROLE dcms_grafana SET default_transaction_read_only = on;

-- pg_stat_statements and the pg_stat_* views, for the Postgres dashboard. These expose
-- query shapes and timings, never row values.
GRANT pg_monitor TO dcms_grafana;

CREATE SCHEMA IF NOT EXISTS obs;
GRANT USAGE ON SCHEMA obs TO dcms_grafana;

-- Deliberately no grants on any other schema. The views in obs are owned by the superuser
-- and therefore run with its rights, so the role reaches exactly the columns a view chose to
-- expose and not one more.
SQL

echo "Grafana reporting role provisioned."
