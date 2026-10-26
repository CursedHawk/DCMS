# ADR 0026: Environments are tenants of one workspace; configuration moves between them as reviewed migrations

**Status:** accepted (2026-10-26) · in progress (kanban epic #351)

## Context

A workspace had exactly one copy of everything. The only place to try something out was the
**preview sandbox**: an `IsSandbox` flag on six tables (visitor accounts and refresh tokens, chat
conversations and messages, form submissions, `cms.plugin_data`), set by `X-Dcms-Sandbox`, which
only the admin preview proxy sent. It isolated what visitors typed into a preview and nothing
else: no configuration, no code, no content, and it never reached RLS.
[Publishing branches](../publishing-branches.md) give a site's git branches their own domains,
but every branch reads the workspace's live content and plugins.

Teams want the usual development → test → staging → production pipeline: separate data per
stage, changes that move forward deliberately, and a production that changes only through the
pipeline.

## Decision

### An environment is a tenant row

Every isolation layer DCMS has is keyed on the tenant id:
- EF query filters and forced RLS ([0015](0015-forced-rls.md)),
- MinIO prefixes `tenants/{id}/…`, Redis keys `t:{id}:…`,
- the identity realm, the Forgejo org per slug,
- NATS payloads and the `TenantDeleter` sweep.

A new tenant row inherits all of them unchanged. An environment column on every tenant table
would have touched 60 RLS tables, 53 entity types, 211 `IgnoreQueryFilters` calls and every key
format, and `IsSandbox` showed where that ends: six tables, EF only.

`tenancy.tenants` gains:
- `WorkspaceId` — the root tenant's id; null while environments are off,
- `EnvironmentName` (`production` on the root), `EnvironmentOrder`,
- `PromotesFrom uuid[]` — the environments allowed to promote into this one.

**The root tenant is the production environment.** Its id and slug never change, so domains,
the Forgejo org, the realm and every stored link keep working. Other environments are slugged
`{rootSlug}-{name}`. `X-Dcms-Tenant` still carries a slug, so switching environment is switching
tenant and nothing downstream (workers, site-builder, site-host, content-api host resolution)
learns a new concept. A workspace that never enables environments is exactly what it was.

Storage quota is enforced on the workspace's sum, so environments do not multiply it. Suspending
or deleting the workspace cascades to its environments.

### Members are the workspace's; roles are the environment's

Invitations and removals happen on the root and are reconciled into every environment. Roles
stay per tenant, so a member can edit in development and have no role — and therefore no access —
in production. The workspace owner is owner everywhere.

### Configuration moves as migrations, and every migration is reviewed in the app

Only configuration moves: site code and settings, plugin instances and their settings (content
types included), Dynamic Apps models, views and flows, API connection definitions, roles and
User Authentication rules. Content entries, records, submissions, visitors and media never move.

The model is EF's:
- Each environment keeps a **baseline**: the export of its configuration as of its last cut or
  apply.
- **Create migration** diffs the current export against the baseline and records a named
  migration of ordered changes `{kind, key, op, before, after}`; site code is a commit range.
- **Apply** runs, in the source's order, the source's migrations the target has not applied —
  including those the source received from upstream, so one migration id travels
  development → test → production.
- **Rollback** applies each change's `before`, in reverse.

Nothing reaches a target unreviewed. The target's **Deployments** page lists each pending
migration and every change in it as a diff; a change whose `before` no longer matches the
target is a conflict the reviewer resolves (overwrite or skip). The reviewer may stop at any
migration. Applying needs `environments:promote` on the target, runs as a job
([0024](0024-async-jobs.md)), and is audited with both tenant ids and the resolutions.

Kinds are a contract, `IEnvironmentConfigKind` (export / apply), so plugins contribute their own
— Dynamic Apps does. Secrets are never part of a change; copying their ciphertext is an explicit
option at apply time (the Transit keys are global).

### Each environment decides what the pipeline owns

`tenancy.environment_policies` sets, per kind and optionally per item:
- **Mixed** (default) — editable here, and migrations apply too,
- **Local** — editable here, migrations skip it,
- **Managed** — only migrations write it; direct writes answer `409 environment_managed` and a
  Managed site's repository is read-only to collaborators.

"Lock to pipeline" sets every kind to Managed.

### Environments can use each other's resources, explicitly

A link, created in the consuming environment and only within one workspace, lets it use a
target environment's published content, site users (its realm), Dynamic Apps tables, or plugin
service. The linked operation runs under the target tenant, caches under the target's keys, and is
audited with both ids.

### The preview sandbox is deleted

Its rows are deleted and its columns dropped. A preview acts on the environment it belongs to;
safe experiments belong in a development environment. `AuditEvent.IsSandbox` stays, because it is
hashed into the audit chain and historical records must keep verifying. Analytics still ignores
editors' preview traffic through `X-Dcms-Preview`, which the edge scrubs like any internal header.

## Consequences

- An environment costs what a tenant costs, and every guarantee a tenant has is an environment's.
- Slugs and hostnames stay globally unique, so environment slugs are long-ish and each
  environment gets its own managed subdomains.
- Every promotable kind must have a natural key (a slug) and a deterministic export.
- Cross-environment links are the one place a request touches two tenants; they go through one
  resolver and are audited.
- Previewing a site in production now writes live data: a form submitted in preview is a real
  submission and sends its notification.
