#!/bin/sh
# Least-privilege DB role for the edge.
#
# The edge is the public TLS ingress: the one process on this platform that an anonymous request
# from the internet reaches first. It reads and writes exactly one schema -- `edge`, holding
# certificates, the ACME account and the route overlay -- and must never hold the `dcms` owner
# connection, whose leak would expose every tenant's data across every schema.
#
# Note what it deliberately CANNOT read: tenancy.domains. Whether a hostname may be issued a
# certificate is asked of site-host over HTTP (/internal/tls-allowed, and its sibling
# /internal/tls-hostnames). One internal call is cheaper than widening what a compromise here
# reaches.
#
# Unlike dcms_sitebuilder this needs no BYPASSRLS: nothing in the `edge` schema carries a tenant
# column, so no policy applies to it.
#
# Re-applied on every deploy by the postgres-bootstrap job, so it is written idempotently.
set -e

PW="${EDGE_DB_PASSWORD:-dcms-edge-dev}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=edge_pw="$PW" --set=owner="$POSTGRES_USER" <<'SQL'
-- Create the role only if missing (psql interpolates :'edge_pw' outside dollar-quotes; it would
-- NOT inside a DO $$...$$ body, so \gexec is used instead of a DO block).
SELECT format('CREATE ROLE dcms_edge LOGIN PASSWORD %L NOSUPERUSER', :'edge_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_edge')
\gexec

-- Always converge the password/attributes for an existing role.
ALTER ROLE dcms_edge LOGIN PASSWORD :'edge_pw' NOSUPERUSER;

GRANT USAGE ON SCHEMA edge TO dcms_edge;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA edge TO dcms_edge;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA edge TO dcms_edge;

-- Tables and sequences that admin-api's EF migrations create later (owned by the dcms owner)
-- become accessible to the edge role automatically.
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA edge
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO dcms_edge;
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA edge
  GRANT USAGE, SELECT ON SEQUENCES TO dcms_edge;
SQL

echo "edge DB role provisioned."
