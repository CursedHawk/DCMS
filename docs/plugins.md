# Writing a DCMS plugin


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

## Registering it

Add the plugin to `DcmsPluginSet.AddAll` in `src/Plugins/Dcms.Plugins.All`, and a
project reference from that project. `Dcms.Plugins.All` is the single source of
truth for both content-api (runtime) and admin-api (catalog), so registering
there is all that is needed — content-api calls `AddDcmsPlugins(p => p.AddAll())`
and there is no runtime DLL loading. Add manifest tests to
`tests/Dcms.PluginSdk.Tests`.

If the plugin stores tenant rows in a new table, add it to
`RlsConfigurator.TenantTables` (`src/Shared/Dcms.Shared.Data/Rls`). Grants to the
least-privilege `dcms_rls` role are schema-wide defaults while the RLS policies are
opt-in per table, so an entry that is missing is a grant with nothing enforcing the
tenant predicate. `RlsConfigurator.AssertCoverage` fails admin-api's startup when a
mapped entity carries a `TenantId` and appears in neither `TenantTables` nor
`ExemptTables` — if a cross-tenant scan genuinely needs the table unfiltered, add it
to `ExemptTables` with a comment saying why.
