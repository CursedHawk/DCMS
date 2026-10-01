# ADR 0017: Every plugin is an API — packages, open contracts, hooks, installed plugins

**Status:** accepted · implemented (2026-09-30)

## Context

[ADR 0016](0016-plugin-contracts.md) gave plugins contracts, platform services and a
dispatcher, and migrated four plugins onto them. The rest still lived in the hosts: the
analytics beacon, ingest and dashboard; the branding endpoint; the chat hub, assistant and
console; the whole Meta integration; the form review screen; and a TypeScript client
generator, a site publish and a slug validator that each recognised plugins by id. Contracts
lived inside plugin assemblies, so using one meant referencing the plugin itself (Forms
referenced VisitorAuth whole). A contract had exactly one provider, nothing could intercept an
operation before it happened, content plugins exposed no data API, and adding a plugin meant a
platform release.

The goal: an ecosystem in the spirit of a game server's plugin API — every plugin exposes its
calls, events and data; any plugin (including one written outside this repository against
published packages) can use them from C#; the platform is configurable and extensible without
knowing any plugin by name.

## Decision

### The hosts know no plugin

Everything a plugin does lives in the plugin. `IPlugin` keeps only `Manifest` mandatory;
`ConfigureServices(services, PluginHost)` learns the plane (site / admin) and the operator's
`Plugins:{id}` settings, so a plugin registers per host what it runs there. `MapHostEndpoints`
covers routes that belong to no instance (the chat hub, the Meta OAuth callback, a tenant-wide
dashboard). What the platform needs to know about a plugin it reads from the manifest:
`ClientBindings` (members of the generated TypeScript client served by runtime helpers),
`ReservedSlugs` (instance slugs its host routes shadow), `TracksVisitors` (a published site
asks for consent). Plugins register their own CORS policies, SignalR, rate-limit policies and
workers. `HostIndependenceTests` fails the build if a service or shared library names a plugin.

Analytics, Branding, the form review screen, LiveChat and the Meta feeds moved into their
plugins. Cross-tenant analytics retention stays in admin-api as platform operations; data
schemas stay in `Dcms.Shared.Data`, migrated by the platform.

### Every plugin publishes a `.Api` assembly

Contracts, their records, events, hooks and permission constants live in
`Dcms.Plugins.{Name}.Api`, which may reference the SDK abstractions only (tested). Consumers
reference the `.Api`, never the plugin. Content plugins got typed contracts over their data
(`blog.posts@1 → ContentPage<BlogPost>`) via `PublishedContentSource<T>`, and lifecycle events
the platform raises for every publish path by bridging `content.published`/`unpublished`.

### Contracts are open

A contract is its interface: several plugins may provide it, as long as they implement the one
declaring interface. `Get<T>` resolves the bound or sole instance across providers;
`GetAll<T>` returns every enabled one — the extension point. A required consume is satisfied by
any provider. `PluginDependency` is gone; dependencies are derived from `Consumes`.

### Hooks: interception before the fact

A contract may declare hook records its provider runs in-process before acting
(`ctx.Hooks.RunAsync`). Plugins consuming the contract intercept them in priority order and may
rewrite the payload or cancel; the first cancel ends the chain. Hooks are fail-open — a
throwing or slow interceptor (`Plugins:HookTimeoutMs`) is skipped — because they ask other
plugins' opinion about an operation the provider owns. Forms runs `forms.submitting`.

### Platform services grew where plugins needed them

`dcms.media@1` resolves media and imports a remote file through the upload pipeline (admin
plane only, https to public addresses only — every connection is checked, so redirects and DNS tricks cannot reach the internal network). Platform contracts are first-registration-wins so a host supplies its
own implementation (admin-api's import). The Meta mirror, sync and reauth notices moved onto
`dcms.media`, `PluginHandlerRunner` and `dcms.notifications`; chat notifications became the
LiveChat plugin's subscription to its own event.

### Installed plugins and packages

Besides the compiled-in set, both hosts load every published plugin under `Plugins:Directory`
(compose mounts `./plugins`). Each gets its own `AssemblyLoadContext`, but assemblies the host
has — SDK, `.Api` packages, framework — resolve to the host's copy so types unify. A different
SDK major, or a folder that is not a published plugin, stops startup. `Plugins:Disabled`
switches any plugin off per deployment.

CI packs the SDK abstractions, the kernel and every `.Api` as NuGet packages
(`major.minor.pipeline`, assembly version `major.0.0.0`) into the project's package registry
when their sources change. The sample plugin (the Greeter then, the Guestbook since ADR 0019)
is built only against those.

### Self-describing

`GET /api/admin/marketplace/{id}/reference` describes a plugin from the running registry —
contracts with C# signatures and JSON Schemas, events, hooks, content, config, consumers and
providers, packages and generated C# — and the admin's API reference page renders it and runs
admin-exposed operations against an instance.

## Consequences

- A plugin is added by reference (built-in) or by folder (installed); neither needs a host
  change. A capability the hosts must honour is a manifest field, not a special case.
- The public surface of the ecosystem is the `.Api` packages plus the SDK: breaking one is a
  new contract major (or SDK major), shipped beside the old.
- `roster.members` moved to `@2` (typed results). `chat.message.posted` and the CHAT stream
  are gone; chat traffic is `live-chat.*` events on `PLUGIN_EVENTS`.

### Known limitations

- **Installed plugins are trusted code.** They run in-process with the host's rights; the
  loader checks compatibility, not intent. Out-of-process plugins (the descriptor and
  dispatcher were designed for them in ADR 0016) remain the answer for untrusted code.
- **An installed plugin has no migrations**: `dcms.storage` is its database.
- Hooks run in the provider's process only; a hook interceptor in an installed plugin runs
  wherever the provider runs the hook (Forms: content-api).
- The generated site client's runtime helpers (`ClientBindings`) exist for first-party plugins
  only; an installed plugin's site surface is its described operations.
