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
- automation: `outbox`, `flow_runs`, `flow_run_steps`, `flow_schedules`.

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

### Automation: data, run by the plugin's own worker over a database queue

- **Flows are configuration.** A trigger, a condition and steps, each step one versioned action (`id@major`) with an input of `{{ expression }}` templates. Conditions and inputs use **our own small CEL-like expression language**: a hand-written parser, a whitelisted function set, no reflection, no I/O. It is bounded everywhere: source length, nodes, nesting, evaluation steps, string size, and the rendered size of a step's input. Reads never copy, so naming a large record repeatedly costs nothing. A third-party engine would need sandbox vetting; JSON-logic is hostile to people.
- **Every mutation writes an event envelope to `apps.outbox` in its own transaction.**
- **One background worker in admin-api** routes outbox events to the flows they trigger (under the revision the event happened in), starts due schedules, and claims and executes runs.
- **The database is the queue.** `apps.flow_runs` rows are claimed with `FOR UPDATE SKIP LOCKED` and a lease, so replicas share the work and a crashed worker's runs are picked up again.
  - The PLUGIN_JOBS JetStream queue was rejected for this: 4 in flight across all plugins, a 2-minute dedupe window, a 1-day delay cap, and run state in two places.
  - Each run executes inside `PluginHandlerRunner` (tenant scope, enabled-instance check, audit flush) and is pinned to its revision and flow hash, so a publish never changes what a queued run does.
- **Delivery is at least once.** Runs are deduplicated on `(event, flow, flow hash)` by a unique index. A retry resumes after the steps that already succeeded. Side effects carry the step's idempotency key (email and notification dedupe keys).
- **Failures.** Transient failures retry with backoff up to 3 attempts. Bad input, a refused record or a conflict fail at once. A failed or terminated run can be retried by hand.
- **Explicit limits.** Depth 5, at most 20 runs per correlation, 50 steps, 100 record writes and 60 seconds per run. A run refused by a limit is recorded as terminated, with the reason, rather than silently dropped.
- **Actions** come from a fixed catalog:
  - records: create, update, delete, lookup, query;
  - `flow.invoke` (manual flows only) and `event.publish`;
  - `dcms.email.send` and `dcms.notifications.raise`;
  - `content.get` and `content.list`;
  - `visitor.lookup` through `visitors.profiles`.

  Every cross-plugin call goes through the contract proxy. A change of meaning is a new major version alongside the old.
- **Schedules** are rows rewritten at publish: an unchanged interval keeps its next due time. Missed ticks collapse into one.

### The public API and its OpenAPI come from the published model only

- **Routes.**
  - Content-api serves `/api/{slug}/data/{table}[/{id}[/{navigation}]]` and `/api/{slug}/_model`.
  - The literal `data/` segment is required: content delivery owns `/api/{slug}/{contentType}` for every plugin instance, and a bare `{table}` parameter there is ambiguous.
  - Every call goes through `RecordService` on the public plane.
- **Each table's `public` access decides what the site may do with it:**
  - **Read: none.** The table does not exist for the site (404).
  - **Read: own.** The call needs a signed-in visitor (401) and only ever sees that visitor's records.
  - **Writes.** Creating needs `create`, and the visitor becomes the owner. Changing or deleting needs `updateOwn` / `deleteOwn` and only works on the visitor's own records.
  - **Fields.** Hidden fields are never served, filtered or accepted, and read-only fields only by automations.
- **Write safeguards.** Writes from a site preview are refused. Public writes are throttled, at 120 an hour per app and client.
- **OpenAPI.** The tenant's OpenAPI document and generated TypeScript client describe exactly that surface, tagged with the model revision.
  - The SDK gained `IPlugin.BuildOpenApiFragmentAsync` (an async, service-aware fragment) and `IPlugin.ApiVersionAsync` (here, the published revision's hash), which both hosts add to the document's and the client's cache keys. Publishing therefore rebuilds them at once.
  - The document is descriptive: authorization is the routes' job.

### The assistant is a client of the same control plane

Configuration, automation runs and records are **contract operations exposed to `Ai`** — the
existing and only tool path:
- `dynamic-apps.config@1`: summary, table and flow, actions, revisions, diff, preview, public API, apply change set, validate, discard, publish, rollback, runs, start and retry flows;
- `dynamic-apps.records@1`: query, get, create, update, link, unlink, bulk update, delete, bulk delete.

- **One set of services.** The implementation is the same `ConfigurationService`, `RecordService` and `FlowRunService` calls the admin routes make. A person and the assistant are held to the same rules, and each operation's `Permission` is the one the matching admin route requires.
- **Risk follows the existing mode table.** Inspection is read. Draft edits and record writes are safe. Discard, publish, rollback, record deletion, bulk changes and running flows are dangerous.
- **Hiding a tool is ergonomics.** The dispatcher re-checks every call.
- **Untrusted text.** Record text and run data are marked as external text, and the assistant fences them as untrusted.
- **Tool descriptions teach the change-set grammar and the draft-first workflow.** The configuration page adds a short system-prompt section to the same effect.
- **Trace.** An agent's call is marked `?plane=ai` and carries `X-Dcms-Ai-Conversation`, `-Run` (one per question) and `-Tool-Call` headers.
  - The revision stores the conversation and run, the change log stores all three, and the audit entry names them.
  - This is attribution only: what the call may do is still the member's permissions.
- **The console assistant gained what plugin tools needed:**
  - result capping;
  - approval cards that say what a call will do (a change set lists its operations);
  - invalidation of the `plugin:{slug}` query keys;
  - a write mode for members whose only writable tools are plugin ones.

Not done: a separate `ai-configure` / `ai-publish` permission to narrow what may be delegated (the
instance's AI-tools switch and the member's own permissions decide), and storing an `ai.runs` row
per console question (its id is sent, not recorded).

### Admin UI: plugin-shipped screens

Per ADR 0019 the plugin ships its own instance screens (`configuration`, `data`) from
`src/Plugins/Dcms.Plugins.DynamicApps/admin`. The host gains only small, reusable additions:
- a plugin screen can set the assistant's page context;
- a plugin screen can open the assistant.

An instance screen also requires `plugins:manage` (the `/plugins/{slug}` route guard). That is
accepted for now.

## Consequences

- **A new tenant table must still be hand-registered for RLS.** Every Dynamic Apps table goes through `RlsConfigurator.TenantTables` (or `ExemptTables` for the cross-tenant outbox and schedules) like any other.
- **OpenAPI for a plugin can no longer be built from instance config alone.** The SDK gained an async fragment and an API-version hook so publishing invalidates cached documents and generated clients.
- **`data` and `_model` are now reserved as a plugin's second path segment.** Matching happens before the instance is known, so the literal routes outrank content delivery's `/api/{slug}/{contentType}/{itemSlug}` for every slug. A content type named `data` would be unreachable in any plugin; no built-in plugin has one.
- **Some limits are known and accepted for now:**
  - Sorting on unindexed jsonb fields is unindexed.
  - Row and field policies are table-level exposure plus hidden/read-only fields.
  - Richer policies, calculated fields, webhooks, imports and sandboxed WASM actions are later work.
- **This is the first plugin with `Safe` and `Dangerous` operations exposed to the assistant.** The console assistant's contract-tool path gets the summaries, cache invalidation and result caps it lacked.
