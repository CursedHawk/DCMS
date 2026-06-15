# ADR 0003: Tenancy & authorization data owned by admin-api

**Status:** accepted (2026-06-13) · **Phase:** 3

## Context

The original plan placed tenant memberships, tenant roles, role permissions and
invitations in the `identity` schema owned by the identity service, with the
access token carrying a `tenant_ids` claim.

In practice every management operation (tenant CRUD, member/role/permission
management, invitations) is driven by the admin UI, which calls **admin-api**;
and permission evaluation also happens in admin-api. Keeping the data in identity
would force a cross-service hop for every management write and every permission
check.

## Decision

- The **tenancy** schema (tenants, domains, tenant_memberships, tenant_roles,
  tenant_role_permissions, member_roles, invitations) is owned by **admin-api**
  and defined in `Dcms.Shared.Data` (so content-api can also resolve tenants by
  host in later phases). admin-api applies the migration on startup.
- The identity service stays pure authentication: global users, global roles,
  OpenIddict. Tokens carry `sub` + global roles only — **no `tenant_ids` claim**.
- The SPA learns its tenants from `GET /api/admin/me/tenants`. The current tenant
  is selected explicitly via the `X-Dcms-Tenant` header (Finbuckle header
  strategy); content-api will use the host strategy.
- Permission evaluation runs in admin-api against its own DB + a Redis cache
  (`perm:{tenantId}:{userId}`), invalidated on `membership.changed`.

## Consequences

- No cross-service hop for management or permission checks.
- Tokens never carry stale tenant/permission data.
- Identity and tenancy share one Postgres database; the `user_id` column in
  `tenant_memberships` references `identity.asp_net_users` without a DB FK
  (different DbContext) — validated at the application layer.
