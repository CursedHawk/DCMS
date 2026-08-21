-- Row-Level Security backstop (Phase 12). Defense-in-depth *on top of* the EF
-- Core per-tenant query filters, which remain the primary isolation guarantee.
--
-- The application connects as the table owner (dcms) and therefore bypasses RLS
-- by default — RLS does not change app behaviour. The policies are enforced for
-- any *non-owner* role that reads the data with a raw connection. `dcms_rls` is
-- that least-privilege role: it has no BYPASSRLS, so a query it runs only sees
-- rows whose TenantId matches the `app.tenant_id` GUC. The Phase 12 isolation
-- test connects as this role to prove the policies are correct, and it is the
-- role a future fully-RLS-enforced deployment would run the app under.
--
-- The policies themselves (ENABLE ROW LEVEL SECURITY + CREATE POLICY per table)
-- are applied at runtime by RlsConfigurator after EF migrations create the
-- tables, since the tables don't exist yet at container-init time.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_rls') THEN
        CREATE ROLE dcms_rls LOGIN PASSWORD 'dcms-rls-dev' NOSUPERUSER NOBYPASSRLS;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA tenancy, plugins, cms, media, sites, search, analytics, chat, visitors, ai, forms, audit TO dcms_rls;

-- Future tables (created by EF migrations) become readable by the test role.
ALTER DEFAULT PRIVILEGES FOR ROLE dcms IN SCHEMA tenancy, plugins, cms, media, sites, search, analytics, chat, visitors, ai, forms, audit
    GRANT SELECT ON TABLES TO dcms_rls;
