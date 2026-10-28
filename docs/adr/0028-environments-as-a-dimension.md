# ADR 0028: Environments are a dimension of one tenant

**Status:** accepted (2026-10-27) · in progress (kanban epic #376) · supersedes the "an
environment is a tenant row" part of [0026](0026-environments.md)

## Context

[ADR 0026](0026-environments.md) built environments as tenants: every environment was its own
row in `tenancy.tenants`, grouped by `WorkspaceId`. That bought isolation for free, but made an
environment a second workspace in all but name:
- a media library of its own,
- members mirrored in from the root,
- its own AI settings, notifications and audit log.

That is not what an environment is to the people using one. An environment is a stage, or a
purpose, of **one** workspace:
- They share the media library, the people who administer it, and the workspace's settings.
- They differ only in what the workspace *builds*: content, plugins and their settings, sites,
  Dynamic Apps, roles and access rules.
- What differs moves between them: in deployments along a pipeline, by duplicating an
  environment, and by branching a feature from one and merging it back.
- Some environments are not stages at all but services the others use.

## Decision

### An environment is a row of the tenant, and a column on what it scopes

`tenancy.environments` holds a tenant's environments:
- name, order, `Purpose` (`stage`, `feature` or `service`),
- the environments allowed to deploy into it (`PromotesFrom`),
- what a feature was branched from (`ForkedFrom`),
- whether merging it offers only what it changed itself, and whether it goes once merged.

**Production's environment id is the tenant id.** Every existing row is backfilled with
`EnvironmentId = TenantId`. Identity realms, Redis keys, MinIO prefixes and edge rows keyed by
the tenant id are therefore already production's, and nothing moves for a workspace that never
adds an environment.

**What is scoped per environment:** tables whose rows carry `EnvironmentId` as well as
`TenantId`, through `EnvironmentEntity`.
- Plugin instances, their data and secrets.
- Content items and versions, scheduled publishes, the content outbox.
- API connections and snapshots.
- Sites, their builds, drafts and publishing branches. Domains, since a domain serves one site.
- Dynamic Apps, entirely.
- User Authentication's roles, grants, gates and API rules.
- Search documents, visitors, chat, form submissions, social connections.
- Edge site gates, which are host-keyed and outside RLS: their `TenantId` is the environment's id,
  which is also its realm's.
- Roles, their permissions and members' role assignments.
- Saved table views.
- The environment engine's own tables.

**What is the workspace's:** tables with `TenantId` only.
- The member list and invitations.
- The media library, with one quota.
- AI settings and assistant chats.
- Notifications.
- Analytics events, which name their site and through it their environment.
- Usage.
- The audit log, which records the environment in each event's details. The hash chain's format
  does not change.
- Jobs, which carry the environment as a nullable column.

### How a request knows its environment
- The admin console sends `X-Dcms-Environment`, the environment's name.
- On a site, the domain names its site, and the site its environment. The preview proxy passes
  the environment it previews.
- The edge scrubs the header on public routes.
- `ITenantContext.EnvironmentId` defaults to production.
- The EF filters of every `EnvironmentEntity` add `EnvironmentId = current`, and inserts are
  stamped with it.
- `app.environment_id` joins `app.tenant_id` as a GUC. The row-level security policy of every
  environment table requires both.
- Environment-scoped cache keys, event payloads, jobs and plugin storage are keyed by the
  environment id. Production's are therefore unchanged.

Permissions are resolved for (tenant, environment, user). A member's roles in one environment
say nothing about another. Once a workspace has more than production, a member gets into an
environment only with a role there. Owner is held in production and mirrored into every other
environment; it cannot be granted or revoked anywhere else. Members are added and removed in
production only, so someone who manages members in development cannot touch production's.

Configuration kinds (`IEnvironmentConfigKind`) still take the tenant's id: the engine calls each
one inside `RlsScope.Environment` for the environment it works on, and a kind's
`RlsScope.Tenant(tenantId)` blocks inherit it. One realm per environment, keyed by its id; a
non-production realm's slug is the tenant's followed by the environment's name.

### What builds on it
- **Deployments** keep ADR 0026's model: baselines, named migrations, review, apply, rollback,
  and ownership policies as the lock on upper environments. Changes merge field by field (a
  three-way merge, with conflicts decided per path), and a migration can be cut from a chosen
  subset of what changed.
- **Content** is a kind like configuration: chosen per deployment, reviewed per entry, and
  published on apply. Media needs no copying, because the library is shared.
- **Duplicating** an environment copies a chosen scope: configuration and content, Dynamic Apps
  models and their records, and optionally plugin data, submissions, visitors, chat and
  analytics. Every id is remapped. Host modules and plugins contribute copiers
  (`IEnvironmentDataCopier`).
- **Feature environments** are branched from an environment. Merging one offers only what it
  changed, and site code merges as its commit range.
- **Service environments** are outside the pipeline. Other environments of the workspace use
  them through environment links:
  - `app` — an app's `/api`,
  - `users` — sign-in,
  - `contracts` — in-process plugin contracts, run entirely as the service environment, with an
    allow-list of operations, audited on both sides, never chained,
  - `tables` — Dynamic Apps tables with per-table grants.

  Links are configuration, so they deploy with the pipeline.

### Moving from ADR 0026
A deploy-time step converts each environment tenant into an environment of its workspace:
- its rows are re-keyed to the workspace's tenant id with the old tenant id as their environment
  id, so realms, edge rows, Redis keys and plugin files keyed by it stay valid,
- its media rows move to the workspace and keep their object keys, so nothing is copied,
- its mirrored memberships are dropped, and its role assignments move to the workspace's
  membership of the same person,
- its invitations, usage and AI settings are dropped; its audit rows stay as written, because the
  log is append-only,
- its tenant row is deleted.

The step (`EnvironmentConversion`) runs at startup after every migration, and is a no-op once it
has dropped the old columns.

Environment slugs, `WorkspaceId` and the per-environment tenant rows go away.

## Consequences

- Every query on an environment table carries the environment, enforced twice: by the EF filter
  and by the row-level security policy. A coverage check fails startup for an environment table
  missing from the policy list, as it does for tenant tables.
- Code that reads across tenants with an explicit tenant predicate must name the environment
  too. A test fails on one that does not.
- An environment can be duplicated or deleted without touching the media library or the member
  list.
- Workspace-level things now live in one place: quota, members, AI keys, audit.
