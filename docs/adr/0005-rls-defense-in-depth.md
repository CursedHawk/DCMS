# ADR 0005: Postgres RLS as a defense-in-depth backstop (owner-bypass model)

**Status:** accepted (2026-06-15) · **Phase:** 12

## Context

Per-tenant isolation is enforced by EF Core global query filters on the Guid
`TenantId`, applied by every tenant-scoped DbContext and exercised by the
multi-tenancy isolation suite that grows each phase. The plan calls for Postgres
Row-Level Security as **defense-in-depth on top of** those filters, verified by a
raw-`NpgsqlConnection` test that bypasses EF.

The wrinkle: the application connects as the table **owner** (`dcms`). In
Postgres a table owner bypasses RLS unless `FORCE ROW LEVEL SECURITY` is set.
Forcing RLS on the app's own connection would require setting an `app.tenant_id`
GUC on *every* connection for *every* context — and would break the legitimate
cross-tenant code paths that use `IgnoreQueryFilters()` (SuperAdmin listings,
`/me/tenants`, invitation accept, the analytics/search/scheduler consumers).
That is a large, regression-prone change for a backstop.

## Decision

- Enable RLS and a `tenant_isolation` policy on every tenant-scoped table
  (`RlsConfigurator`, run after migrations; idempotent). The policy keys on a
  GUC: a row is visible/insertable only when
  `"TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid`.
- Do **not** `FORCE` RLS. The app keeps connecting as the owner and is unaffected
  — **EF query filters remain the primary and only behavioural isolation guard**.
- Provision a least-privilege role **`dcms_rls`** (`NOBYPASSRLS`, `SELECT` only)
  in `infra/postgres/init/01-rls.sql`. Any raw access through this role is fully
  subject to the policy. The Phase 12 isolation test connects as `dcms_rls`, sets
  `app.tenant_id`, and asserts it can read only the matching tenant's rows (and
  none when the GUC is unset).
- Cross-tenant scan tables (`content_outbox`, `scheduled_publishes`) are excluded.

## Consequences

- Zero risk to the eleven phases already working: the running services see no
  behavioural change.
- The policies are real and proven correct by the raw-SQL test; they protect any
  non-owner / future least-privilege access path.
- To make RLS protect the app **directly** (true forced enforcement), a later
  step runs the services under `dcms_rls`-style roles and sets the `app.tenant_id`
  GUC per connection (a `DbConnectionInterceptor` reading `ITenantContext`,
  bypassing for the documented cross-tenant paths). Deferred to avoid
  destabilizing the MVP; the GUC-based policy is already shaped for it.
