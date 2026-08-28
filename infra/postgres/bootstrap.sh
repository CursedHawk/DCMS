#!/bin/sh
#
# Applies the cluster-level bootstrap -- schemas, extensions and the three non-owner roles --
# to an ALREADY-RUNNING Postgres.
#
# The scripts in init/ are mounted into /docker-entrypoint-initdb.d, which Postgres runs
# exactly once, on an empty data directory. Every one of them is written to be idempotent
# (CREATE SCHEMA IF NOT EXISTS, \gexec guards, converging ALTER ROLE), so the only thing
# stopping them from being re-applied is that nothing ever re-applies them.
#
# That gap is not theoretical. vps1 was provisioned long ago, so every role and extension
# added since had to be created by hand against production -- and a role that exists on a
# fresh developer cluster but not on vps1 fails at runtime, far from the change that
# introduced it. site-builder's `42501: permission denied for schema plugins` is exactly
# that shape.
#
# Run by the deploy pipeline before the migration jobs: roles and schemas first, then EF
# migrations create tables inside them, then RlsConfigurator applies policies to those tables.
#
# Safe to run against a live cluster. It creates nothing that exists and grants nothing that
# is already granted.

set -eu

: "${PGHOST:=postgres}"
: "${POSTGRES_USER:=dcms}"
: "${POSTGRES_DB:=dcms}"

export PGUSER="$POSTGRES_USER"
export PGDATABASE="$POSTGRES_DB"

echo "postgres-bootstrap: waiting for $PGHOST to accept connections"
i=0
until pg_isready -h "$PGHOST" -U "$POSTGRES_USER" -d "$POSTGRES_DB" >/dev/null 2>&1; do
  i=$((i + 1))
  if [ "$i" -ge 60 ]; then
    echo "postgres-bootstrap: $PGHOST did not become ready" >&2
    exit 1
  fi
  sleep 2
done

echo "postgres-bootstrap: schemas and extensions"
psql -v ON_ERROR_STOP=1 -h "$PGHOST" -f /init/00-schemas.sql

echo "postgres-bootstrap: dcms_rls role and grants"
psql -v ON_ERROR_STOP=1 -h "$PGHOST" -f /init/01-rls.sql

# 01-rls.sql creates dcms_rls with a hardcoded development password, because it has no way
# to take a parameter from initdb. On a fresh PRODUCTION cluster that would leave a role with
# SELECT on every tenant schema holding a password published in this repository. Converge it
# here, where a real one is available.
if [ -n "${RLS_DB_PASSWORD:-}" ]; then
  echo "postgres-bootstrap: setting dcms_rls password"

  printf '%s\n' \
    "ALTER ROLE dcms_rls LOGIN PASSWORD :'rls_pw' NOSUPERUSER NOBYPASSRLS;" |
    psql \
      -v ON_ERROR_STOP=1 \
      -h "$PGHOST" \
      --set=rls_pw="$RLS_DB_PASSWORD"
else
  echo "postgres-bootstrap: WARNING: RLS_DB_PASSWORD unset; dcms_rls keeps the password from 01-rls.sql." >&2
fi
# These two take their own passwords from the environment and converge them on every run.
echo "postgres-bootstrap: dcms_sitebuilder role"
PGHOST="$PGHOST" sh /init/02-service-roles.sh

echo "postgres-bootstrap: dcms_grafana role"
PGHOST="$PGHOST" sh /init/03-observability-role.sh

echo "postgres-bootstrap: complete."
