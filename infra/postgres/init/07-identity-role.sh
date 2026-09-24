#!/bin/sh
# identity's runtime connection (ADR 0015 phase 5): the last service to stop connecting as the
# table owner, `dcms`, which after this only the migrate jobs hold.
#
# A role of its own rather than dcms_app. identity's schema holds password hashes, OpenIddict
# tokens and the refresh-token families; granting it to dcms_app would hand all of that to
# every tenant-facing service. The reverse holds too: identity reads no tenant table, so it
# gets none.
#
# What it gets: DML on `identity`; DML on `dataprotection`, the key ring its cookies and the
# Forgejo password outbox are protected with; and SELECT, INSERT on audit.audit_outbox, which
# is how its audit records leave (admin-api chains them). No other audit table. The outbox grant
# is repeated by the migrate job (AuditSchemaConfigurator), because on a fresh cluster the
# table does not exist yet when this runs.
#
# Re-applied on every deploy by the postgres-bootstrap job, so it is written idempotently.
set -e

PW="${IDENTITY_DB_PASSWORD:-dcms-identity-dev}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=identity_pw="$PW" --set=owner="$POSTGRES_USER" <<'SQL'
SELECT format('CREATE ROLE dcms_identity LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS', :'identity_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_identity')
\gexec
ALTER ROLE dcms_identity LOGIN PASSWORD :'identity_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

GRANT USAGE ON SCHEMA identity, dataprotection, audit TO dcms_identity;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity, dataprotection TO dcms_identity;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity, dataprotection TO dcms_identity;
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA identity, dataprotection
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO dcms_identity;
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA identity, dataprotection
  GRANT USAGE, SELECT ON SEQUENCES TO dcms_identity;

SELECT 'GRANT SELECT, INSERT ON audit.audit_outbox TO dcms_identity'
WHERE to_regclass('audit.audit_outbox') IS NOT NULL
\gexec
SQL

echo "postgres-bootstrap: dcms_identity role ready"
