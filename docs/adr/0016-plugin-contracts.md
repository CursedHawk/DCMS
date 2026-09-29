# ADR 0016: Plugins own their code and meet through versioned contracts

**Status:** accepted · implemented (2026-09-29)

## Context

Until this change a plugin was a manifest. All eighteen had an empty `ConfigureServices`;
their behaviour lived in about ten host files in content-api and admin-api, each recognising
"its" plugin by a hardcoded id string and re-running the same instance lookup. The plugin SDK
declared five runtime services and four of them had no implementation. `PluginDependency` was
displayed in the marketplace and enforced nowhere. Plugins that needed each other did it by
convention: Events held a free-text Roster slug, the chatbot read the search table directly
with the query filters switched off, `ContentRef` was stored and never resolved. VisitorAuth
had no profile, and nothing outside `/me` could ask who the visitor was.

The goal was an ecosystem in which plugins expose what they can do to each other, to the
public site, to the admin and to AI agents, and reach platform services (storage, email,
notifications, queues, jobs…) through one controlled surface — built first-party and
compiled in, but without closing the door on out-of-process third-party plugins later.

## Decision

### A contract is an interface with a transport-neutral description

A contract is a C# interface marked `[DcmsContract("name", major)]`, id `name@major`. Every
method is one operation, marked `[Operation(risk)]` — the risk is a required constructor
argument, so a write can never default to a read — with optional `Permission`, `Expose`
(`Site | Admin | Ai`; always internal) and `ReturnsExternalText`. An operation takes one input
record (or nothing) plus a `CancellationToken` and returns `Task` or `Task<T>`. Events are
records marked `[ContractEvent("name")]` listed on the contract.

At startup the runtime reflects each contract into a `ContractDescriptor`: plain JSON with
input and output as JSON Schema (`JsonSchemaExporter`, stdlib). **The descriptor is the
contract; the interface is its in-process binding.** Everything outside the process — the
site's generated client, the admin catalog, AI tools, and later a remote plugin — works from
the descriptor. Minor versions are additive; a breaking change is a new major, which a
provider can ship beside the old one.

### Manifests say what they provide and what they need

`PluginManifest` gains `Provides` (contract + implementation), `Consumes` (contract id,
optional, binding config key), `Subscribes` (event handlers) and `Jobs` (named handlers,
optionally on an interval). The registry refuses to start on: a malformed or duplicated
contract, a plugin providing a reserved `dcms.*` contract, a required contract nothing
provides, a cycle of required contracts, an event nobody declares, mistyped handlers.

**`Consumes` is the grant list and is enforced in-process.** `IPluginContext.Contracts.Get<T>()`
throws for anything the caller did not declare. Providers are never registered in DI; the
runtime constructs them per resolution and hands back a `DispatchProxy` that traces every
call (`dcms.contract`) and audits every Safe or Dangerous one (`plugin.contract.invoked`).

When several instances provide a contract, the consumer's `BindingConfigKey` names the config
field holding the chosen instance — by id **or slug** (Events keeps its public `rosterSlug`).
The schema property carries `"x-dcms-contract-binding": "<id>"` and the admin renders a picker.
Enabling a plugin whose required provider has no enabled instance, or disabling the last
provider an enabled plugin needs, is a 409 naming the plugins involved.

### Platform services are contracts too

`dcms.storage` (per-plugin JSON documents, `plugins.plugin_data`), `dcms.secrets`
(Transit-encrypted instance credentials, admin-api only), `dcms.blobs`, `dcms.cache`,
`dcms.email`, `dcms.notifications`, `dcms.events`, `dcms.jobs`, `dcms.content`,
`dcms.search`, `dcms.ai`. Each stamps the **caller's** tenant and plugin id onto what it
touches — a row, a key prefix, a message field — and never reads them from input; for the
backends without RLS (object storage, Redis, the bus) that stamp is the whole isolation story.
Every store scopes itself (`RlsScope.Tenant` + an explicit predicate), and `CmsDbContext`,
`VisitorsDbContext` and `FormsDbContext` honour an enclosing `RlsScope.Tenant` in their query
filters, so jobs and event handlers behave like requests.

`dcms.search` is a platform contract, not the Search plugin's: the platform maintains the
index for every tenant whether or not Search is installed, and the chatbot must keep
grounding its answers either way.

### Plugins own their code

`IPluginEndpointBuilder` maps real routes (`MapGet/Post/Put/Delete`) under `/api/{slug}`
(content-api) and `/api/admin/plugins/{slug}` (admin-api, `MapAdminEndpoints`), each behind a
filter that resolves an *enabled instance of that plugin* or answers 404, and establishes the
plugin's `IPluginContext`. Two plugins mapping the same route fail startup. admin-api hosts
plugins in full: admin routes, event handlers (one durable per subscription on
`PLUGIN_EVENTS`), jobs (`PLUGIN_JOBS` work queue) and a leader-elected interval scheduler.
VisitorAuth, Forms, Search and Roster are the reference migrations.

### One way in from outside: the dispatcher

`ContractDispatcher` takes (contract id, operation, JSON input, plane) and enforces in order:
the operation exists **and is exposed on this plane** (404 otherwise — hidden operations are
not discoverable); the caller holds its `Permission` (403); the input binds **strictly** to
the operation's own input record (required, non-null, no unknown members; 400). It then
invokes through the same proxy plugins use and maps contract exceptions to 400/409/429.

- Site plane (content-api): `POST /api/{slug}/_contracts/{id}/{op}`. The slug picks the
  provider instance, so these operations appear in the tenant's OpenAPI document beside the
  instance's other routes and the generated client names them
  (`api.members.identity.getCurrent()`). Operations naming a permission are refused; platform
  contracts are unreachable.
- Admin/AI plane (admin-api): `GET /api/admin/contracts?plane=admin|ai`,
  `POST /api/admin/contracts/{id}/{op}?instance=&plane=`, checked with the same policy as
  `RequirePermission`. The catalog omits what the caller cannot use.

`Operation.Permission` gates these external planes only. In-process, the `Consumes`
declaration is the grant: an anonymous visitor's form submission must be able to queue email.

### AI

A tenant opts each instance in (`plugin_instances.AiToolsEnabled`, off by default). The admin
assistant turns the AI catalog into tools — one per operation per opted-in instance — mapping
`OpRisk` onto its existing mode table and fencing results of `ReturnsExternalText` operations
as untrusted. The server re-checks exposure, opt-in and permission on every call. Plugins use
AI through `dcms.ai` (the tenant's provider and quota via ai-gateway) and offer tools simply by
exposing operations to `Ai`; there is no separate tool API.

## Consequences

- A plugin that needs a table, a secret, an email, a queue or another plugin's data has one
  documented way to get it, and the manifest is a truthful statement of what it can reach.
- Out-of-process plugins later need no new concepts: the same descriptors, the same
  dispatcher with a signed service token, platform contracts as their only state.
- Deferred, with the reason recorded: tools for the **site chatbot** (its gateway path is
  text-only for service tokens); a **visitor-gated builder block** (builder sites have no
  sign-in blocks yet); **outbound webhooks** and an **egress allow-list** (only needed for
  third-party plugins).

### Known limitations (from the security review)

- **In-process is trusted.** First-party plugins run in the host process; nothing stops one
  from constructing a platform implementation with a forged context or referencing
  `Dcms.Shared.Data` directly. Enforcement of `Consumes` guards against mistakes, not malice.
  A third-party plugin must run out of process.
- **Plugin data is not sandbox-scoped.** `plugin_data`/`plugin_secrets` have no `IsSandbox`,
  so a plugin writing storage from a site preview writes live data. Tracked as a follow-up.
- **Owners hold every plugin permission.** The Owner role is seeded with platform *and*
  plugin permissions (`OwnerPermissionBackfill.OwnerPermissions`), and the startup backfill
  adds any missing ones to existing tenants — including when a plugin later gains a permission.
  Other roles get `plugin:{id}:{action}` keys through the role editor.
