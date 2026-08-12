#!/bin/sh
# Least-privilege DB role for the site-builder. The builder only reads/writes the
# `sites` schema (build + site rows); it never runs migrations (admin-api owns those)
# and must never hold the `dcms` superuser connection, whose leak would expose every
# tenant's data across all schemas. This role is scoped to the sites schema only.
#
# It DOES need BYPASSRLS: the builder operates cross-tenant with no ambient tenant
# (SitePublishConsumer uses IgnoreQueryFilters), exactly as the owner connection does.
# Scoping it to one schema still removes access to identity/tenancy/cms/media/etc.
#
# Runs only on FIRST cluster init (empty data dir). On an already-initialised cluster
# provision the role manually with the same statements (see docs/runbook.md).
set -e

PW="${SITEBUILDER_DB_PASSWORD:-dcms-sitebuilder-dev}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=sb_pw="$PW" --set=owner="$POSTGRES_USER" <<'SQL'
-- Create the role only if missing (psql interpolates :'sb_pw' outside dollar-quotes;
-- it would NOT inside a DO $$...$$ body, so \gexec is used instead of a DO block).
SELECT format('CREATE ROLE dcms_sitebuilder LOGIN PASSWORD %L NOSUPERUSER BYPASSRLS', :'sb_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_sitebuilder')
\gexec

-- Always converge the password/attributes for an existing role.
ALTER ROLE dcms_sitebuilder LOGIN PASSWORD :'sb_pw' NOSUPERUSER BYPASSRLS;

GRANT USAGE ON SCHEMA sites TO dcms_sitebuilder;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA sites TO dcms_sitebuilder;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA sites TO dcms_sitebuilder;

-- Tables/sequences that admin-api's EF migrations create later (owned by the dcms
-- owner) become accessible to the builder role automatically.
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA sites
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO dcms_sitebuilder;
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA sites
  GRANT USAGE, SELECT ON SEQUENCES TO dcms_sitebuilder;
SQL

echo "site-builder DB role provisioned."
