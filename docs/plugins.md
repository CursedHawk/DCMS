# Writing a DCMS plugin

> Skeleton guide — grows with the SDK (Phases 4–7).

A plugin is a .NET project under `src/Plugins/` referencing
`Dcms.PluginSdk.Abstractions` and implementing `IPlugin`:

1. **Manifest** — stable kebab-case id, semver, description (flows into the
   per-tenant OpenAPI doc), `AllowMultipleInstances`, a JSON Schema for its
   config (drives the admin form), permission definitions
   (`plugin:{id}:{action}`), content type definitions, and dependencies on
   other plugins.
2. **ConfigureServices** — register plugin services (called once at
   content-api startup).
3. **MapEndpoints** — minimal-API routes mounted per enabled instance at
   `/api/{instanceSlug}`, already tenant-scoped. Use `IContentStore`,
   `IMediaResolver`, `IInterPluginResolver` from DI; guard writes with
   `.RequirePermission(...)`.
4. **BuildOpenApiFragment** — describe every operation; the runtime injects
   the admin-authored instance description into each operation description so
   AI consumers understand what *this instance* is for.

## Fields the tenant defines

A content type can carry fields the plugin does not know about — Roster's
`member` is the reference case. Declare a `CustomFieldsDefinition` on the
content type naming two things: the instance-config key holding the field
*definitions*, and the item field their *values* nest under. The admin editor
then renders one input per defined field instead of a raw JSON box.

Values nest rather than spread over the item so a tenant key can never shadow a
plugin field. List the config key in `publicConfigKeys` — a site cannot render
fields whose labels and types it is not allowed to read.

## Public instance config

Instance config is tenant-private. `publicConfigKeys` is an allow-list of keys
served by `GET /api/{slug}/_config`, so a plugin that later gains a credential
cannot start leaking it by accident.

Register the plugin in `src/Services/Dcms.ContentApi/Program.cs`
(`AddDcmsPlugins`) and add a project reference. Add manifest tests to
`tests/Dcms.PluginSdk.Tests`.
