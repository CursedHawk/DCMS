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

Register the plugin in `src/Services/Dcms.ContentApi/Program.cs`
(`AddDcmsPlugins`) and add a project reference. Add manifest tests to
`tests/Dcms.PluginSdk.Tests`.
