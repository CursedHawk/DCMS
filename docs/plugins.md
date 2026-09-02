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

## When a plugin needs a third-party credential

The Instagram and Facebook plugins are the reference case, and the shape is worth copying
because almost none of it lives in the plugin.

**The plugin projects hold no runtime logic at all.** `Dcms.Plugins.Instagram` and
`Dcms.Plugins.Facebook` declare a manifest, content types and a config schema, and nothing
else — the OAuth flow, the Graph client, the sync worker and the media mirroring all live in
`src/Services/Dcms.AdminApi/Social/`. That is not a stylistic choice: `IPluginEndpointBuilder`
refuses `MapGet`/`MapPost` (see `PluginRouteTable`), so bespoke endpoints belong to a host
service. Forms does the same thing in `Dcms.ContentApi/Forms/`.

**Model the third-party data as ordinary content types and most of the work disappears.** A
synced Instagram post is a `cms.content_items` row like any other, so the delivery API, the
Redis cache, the per-tenant OpenAPI document, the GrapesJS block palette and the search index
all serve it with no code in any of those places. Set `Searchable: true` and pick a `SlugField`
that the upstream already guarantees unique — for Meta it is the media id, which is what makes
a re-sync an upsert rather than a diff.

**Credentials never go in instance config.** The config holds a `connectionId`; the token lives
encrypted in `social.meta_connections` under its own Vault Transit key. `publicConfigKeys` then
lists only what a site may read (`accountUsername`, `showStories`) — deliberately not
`connectionId`, which is not a secret in itself but is the handle to one. The plugin tests
assert the allow-list contains no credential key, because that allow-list is the thing standing
between a new config field and a public leak.

**Bound what you pull, in the fetch and not in the retention.** Every Meta instance declares
`maxPosts` / `maxReels` caps with a `maximum` in the JSON Schema that `PluginConfigValidator`
re-checks server-side — a schema is the browser's story, and the config endpoint takes JSON
from anywhere. The caps stop the *pagination*, not just what is kept: an implementation that
downloads an entire account and then keeps the newest five passes every row-count test while
doing exactly the thing the caps exist to prevent, so the tests that matter count upstream
requests (`MetaFeedFetcherTests`).

**A public service must not be able to decrypt a tenant credential.** Instagram stories are
fetched live rather than synced, and content-api — which is internet-facing — has no grant on
`dcms-social-tokens`. It asks admin-api over a client-credentials token scoped `dcms.social`,
and `ServicePrincipalGuard` confines that token to the endpoints that named the scope. See
`docs/vault-secrets.md`.
