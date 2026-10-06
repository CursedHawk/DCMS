# ADR 0021: Dynamic Apps: a tenant-defined application runtime inside the plugin boundary

**Status:** accepted (2026-10-06) · being implemented (kanban epic #208)

## Context

Tenants want to model their own data and processes (a CRM, a ticket desk, event bookings),
not just publish content: tables, fields, relationships, views, automations and an API
generated from them, Dataverse / Dynamics 365 style. They also want to describe the result to
the assistant in plain language and have it build the configuration for them.

DCMS already has most of the platform this needs:
- a plugin boundary with tenant and instance scoping (ADR 0016–0019);
- contracts that are also assistant tools (`OpExposure.Ai`);
- per-tenant RLS (ADR 0015);
- an outbox-and-JetStream event path, plugin jobs and audit.

Two things are missing. The first is a runtime for **metadata the tenant defines at run
time**. The second is a revision system for configuration that a human and an AI can both edit
safely.

The trap is to bend the static mechanisms into dynamic ones: a contract or C# event type per
tenant table, a `JobDeclaration` per flow, the whole model in instance config. All of them are
compile-time plugin metadata, and the tenant's model is data.

## Decision

### A first-party plugin, one instance per application

`Dcms.Plugins.DynamicApps` (+ `.Api`), plugin id `dynamic-apps`, multi-instance.
- **Instance config is bootstrap only:** display name, default locale, default time zone.
- **The application lives in the database**, keyed by tenant + instance, and the instance is always derived from the route slug and the tenant, never from input.
- **The plugin's contracts, jobs and routes are static.** The tenant's tables, fields and flows are data behind them.

### Storage: shared Postgres, schema `apps`

`AppsDbContext` in `Dcms.Shared.Data/DynamicApps`, the Forms pattern:
- RLS is registered in `RlsConfigurator.TenantTables`;
- tables are migrated by `TenancyMigrator`, purged with the tenant and backed up with the cluster;
- `RlsScope.TenantOverride` is honoured for background work.

A dedicated database was rejected because it splits backup/restore and transactions (publish +
audit outbox) and adds a second pool, with no isolation gain over RLS.

**Control plane and data plane are separate tables.** Each capability adds its own tables in its
own migration:
- configuration: `apps`, `revisions`, `changes`;
- records: `records`, `relation_links`, `unique_keys`;
- automation: `outbox`, `flow_runs`, `flow_run_steps`, `schedules`.

### Configuration revisions: one canonical jsonb snapshot per revision

A `revision` holds the **whole application configuration as one canonical JSON document**:
tables, fields, relationships, indexes, choice sets, views, flows, security and settings, with
stable ids. Display names are never identity. Alongside it sit a SHA-256 of the canonical form
and an append-only list of logical **changes** (`+ field deals.amount`).

**Status lifecycle:**
- `Draft` → `Published` → `Superseded` / `RolledBack`, or `Draft` → `Discarded`.
- A database trigger refuses any change to the snapshot or hash once a revision leaves `Draft`.
- A rollback is a **new** revision copied from an older snapshot and published normally. History never moves backwards.
- Revisions carry opaque links to the assistant conversation and run that produced them. The transcript is never the configuration record.

**Rejected alternatives:**
- A normalized revision graph, with versioned rows per table/field/…: heavy to reconstruct and diff.
- A hybrid with a normalized projection: a second representation to keep in sync, for queries the compiled in-memory model answers anyway.

**"Model version", "flow version" and "configuration revision" collapse into one revision
number.** A flow's version is the revision in which its definition last changed. Runs record
`(revision, flowId, flowHash)`.

### One shared draft per application, optimistic concurrency

An application has at most one open draft, based on the current published revision.
- Every edit names the hash it was made against. A stale hash is a 409 carrying the current hash, so the editor re-reads and re-plans, whether a person or the assistant.
- Publishing locks the app row, requires the draft's base still to be the published revision, validates, and moves the published pointer in the same transaction as the audit record and the `revision.published` event.

Per-user/per-run drafts with server-side rebase were rejected: far more conflict logic, and
the assistant and a person editing in parallel should see the same state.

### Records: jsonb canonical store, not EAV, not a table per tenant table

- **Storage.** Each record is one row with a jsonb `Data` column whose values are keyed by **field id** (a lookup by its relationship id), never by api name. A deleted field's values can therefore never resurface under a new field that reuses the name, and a rollback restores access to them.
- **Validation.** Every write goes through one codec on every plane (admin, public site, assistant, automation). It validates and normalizes each value by type: trimmed emails, dates and times in one fixed-width UTC form, choices from their set.
- **Relationships.** N:N links are rows. A lookup is a value in `Data`, found by jsonb containment on a GIN index.
- **Uniqueness.** It is a claim in `unique_keys`: a unique field's value, a unique index's tuple, or a one-to-one target. The database refuses a second claim, so concurrent writers cannot both win.
- **Queries.** They are a validated logical AST compiled to SQL. Field keys and values are all parameters, never interpolated, and arbitrary SQL is never accepted. The AST is bounded: predicates, nesting, one lookup hop, list sizes, page size and depth.
- **Versioning.** Each record carries a version; naming a stale one is a 409.
- **Deletes.** A delete follows each lookup's `onDelete` (restrict, set null, cascade up to 500 records).
- **Publish checks against existing data.** A new required field must already have a value in every existing record. A new unique rule claims keys from the records that exist, and a duplicate refuses the publish.
- **Routes.** Admin routes live at `/_records/{table}` (`_data` is the platform's data-set surface, ADR 0018).
- **Published model only.** Draft metadata never reaches the data plane: the runtime, the public API and OpenAPI read the **published** revision only.

### Automation: data, executed by the plugin's own engine jobs

- **Flows are configuration.** Conditions and inputs use **our own small CEL-like expression language**: a hand-written parser, a whitelisted function set, step and size limits, no reflection, no I/O. A third-party engine would need sandbox vetting; JSON-logic is hostile to people.
- **Every mutation writes an event envelope to `apps.outbox` in its own transaction.**
- **A dispatcher in the style of `OutboxDispatcher` routes envelopes to matching flows.** It records idempotent `flow_runs` and enqueues the plugin's static `automation-run` job. That reuses `PluginHandlerRunner`'s tenant scope, enabled-instance check, audit flush and JetStream retries.
- **Delivery is at least once.** Runs are deduplicated on `(event, flow, flowHash)`, steps on `(run, step, attempt)`, and side effects carry idempotency keys.
- **Explicit limits:** depth, steps, duration, writes and triggered flows per correlation.
- **Actions are versioned** `id@major` from a catalog of native record actions, platform contracts (`dcms.email`, `dcms.notifications`, `dcms.content`, …), `visitors.profiles` and `automation.actions@1` providers. Every cross-plugin call goes through the contract proxy.

### The assistant is a client of the same control plane

Configuration and record operations are **contract operations exposed to `Ai`**
(`dynamic-apps.config@1`, `dynamic-apps.records@1`). This is the existing and only tool path.
- **The admin screens and the assistant call the same services.** The admin screens call them through admin routes, the assistant through those contract operations, with the same permission on each.
- **Risk follows the existing mode table:** inspection is read, draft edits are safe, publish/rollback and record deletion are dangerous.
- **Hiding a tool is ergonomics.** The server re-checks every call.
- **Record text returned to the model is marked as external.**

### Admin UI: plugin-shipped screens

Per ADR 0019 the plugin ships its own instance screens (`configuration`, `data`) from
`src/Plugins/Dcms.Plugins.DynamicApps/admin`. The host gains only small, reusable additions:
- a plugin screen can set the assistant's page context;
- a plugin screen can open the assistant.

An instance screen also requires `plugins:manage` (the `/plugins/{slug}` route guard). That is
accepted for now.

## Consequences

- **A new tenant table must still be hand-registered for RLS.** Every Dynamic Apps table goes through `RlsConfigurator.TenantTables` (or `ExemptTables` for the cross-tenant outbox and schedules) like any other.
- **OpenAPI for a plugin can no longer be built from instance config alone.** The SDK gains an async fragment and an API-version hook so publishing invalidates cached documents and generated clients.
- **Some limits are known and accepted for now:**
  - Sorting on unindexed jsonb fields is unindexed.
  - Row and field policies are table-level exposure plus hidden/read-only fields.
  - Richer policies, calculated fields, webhooks, imports and sandboxed WASM actions are later work.
- **This is the first plugin with `Safe` and `Dangerous` operations exposed to the assistant.** The console assistant's contract-tool path gets the summaries, cache invalidation and result caps it lacked.
