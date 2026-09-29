# Writing a DCMS plugin

A plugin is a .NET project under `src/Plugins/` implementing `IPlugin`. It owns its routes,
its background work and its data, declares the **contracts** it offers other plugins and the
ones it uses, and reaches platform services only through contracts. The design and its limits
are in [ADR 0016](adr/0016-plugin-contracts.md); this page is how to build one.

VisitorAuth (`Dcms.Plugins.VisitorAuth`) is the reference provider and Forms
(`Dcms.Plugins.Forms`) the reference consumer — read them next to this page.

## Anatomy

```csharp
public sealed class ReviewsPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "reviews",                        // stable kebab-case
        name: "Reviews",
        description: "Visitor reviews of products.",   // flows into OpenAPI and AI tool descriptions
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,        // drives the admin form; validated server-side
        permissions: [new PermissionDefinition("moderate", "Moderate reviews")],
        provides: [ContractProvision.Of<IReviews, Reviews>()],
        consumes:
        [
            ContractRequirement.Of<IPluginStorage>(),               // platform: always present
            ContractRequirement.Of<IPluginNotifications>(),
            ContractRequirement.Of<IVisitorIdentity>(optional: true), // another plugin's
        ],
        subscribes: [EventSubscription.Of<VisitorRegistered, WelcomeHandler>()],
        jobs: [JobDeclaration.Of<DigestJob>("digest", TimeSpan.FromHours(24))]);

    public void ConfigureServices(IServiceCollection services) { }

    public void MapEndpoints(IPluginEndpointBuilder endpoints) =>
        endpoints.MapPost("/reviews", async (ReviewInput body, IPluginContext ctx, CancellationToken ct) =>
        {
            await ctx.Contracts.Get<IPluginStorage>().PutAsync(/* … */, ct);
            return Results.Accepted();
        }).PermissionExempt("Anonymous by design: any visitor may review.")
          .WithAudit(AuditActions.ForPlugin("reviews", "submitted"), "review");

    public void MapAdminEndpoints(IPluginEndpointBuilder endpoints) { /* optional */ }

    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance) => OpenApiFragment.Empty;
}
```

Register it in `DcmsPluginSet.AddAll` (`src/Plugins/Dcms.Plugins.All`) with a project reference.
That is the whole wiring: content-api and admin-api both build their registry from it, and the
registry refuses to start on a manifest that does not add up (below).

## Routes

- `MapEndpoints` routes mount under `/api/{instanceSlug}/…` in **content-api** (the public
  plane). `MapAdminEndpoints` routes mount under `/api/admin/plugins/{instanceSlug}/…` in
  **admin-api**, for authenticated members of the tenant.
- A request only reaches the handler for an **enabled instance of this plugin**; anything
  else is a 404. Take `IPluginContext` as a handler parameter: tenant, instance (with its
  config), actor, and `Contracts`.
- `MapContentList` / `MapContentGetBySlug` still declare the generic content delivery routes.
- Two plugins mapping the same method and pattern fail startup.
- Every mutating route must say what gates it — `.RequirePermission(...)`,
  `.RequireVisitor()` (VisitorAuth), or `.PermissionExempt("why")` — and what it records —
  `.WithAudit(...)` or `.AuditExempt("why")`. The coverage tests fail otherwise.
- Reserved instance slugs (`PluginInstanceSlugs`) cannot be taken; they collide with host
  routes or generated-client members.

## Contracts

### Offering one

```csharp
[DcmsContract("reviews", 1, Description = "Product reviews.", Events = [typeof(ReviewPosted)])]
public interface IReviews
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Ai,
        ReturnsExternalText = true, Description = "Approved reviews of a product.")]
    Task<ReviewPage> ListAsync(ReviewQuery input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = "plugin:reviews:moderate", Expose = OpExposure.Admin,
        Description = "Approve or hide a review.")]
    Task ModerateAsync(Moderation input, CancellationToken ct);
}

[ContractEvent("review.posted")]
public sealed record ReviewPosted(Guid ReviewId) : IPluginEvent;
```

Rules the registry enforces at startup:

- Every method is one operation with `[Operation(risk)]`: **Read**, **Safe** (a write a
  person can undo: a draft, a profile field, a queued email) or **Dangerous** (public,
  destructive or irreversible). One input record or none, plus a `CancellationToken`; returns
  `Task` or `Task<T>`. No overloads, no properties.
- Ids are dotted kebab-case plus a major: `reviews@1`. `dcms.*` is reserved for the platform.
  A minor change is additive; anything breaking is `@2`, shipped beside `@1` if consumers need
  time. Event names are dotted kebab-case too (they become NATS subject tokens).
- `Expose` decides who outside the plugin runtime may call the operation (it is always
  callable plugin-to-plugin by a declared consumer):
  - `Site` — the public site, at `POST /api/{slug}/_contracts/{id}/{op}`, and the tenant's
    generated client (`api.{slug}.{contract}.{op}()`). An operation with a `Permission` is
    never served on this plane.
  - `Admin` — members, at `POST /api/admin/contracts/{id}/{op}`, subject to `Permission`.
  - `Ai` — the admin assistant, as a tool, once the tenant opts the instance in.
- `ReturnsExternalText` marks results containing text written by people with no access
  (visitors). AI surfaces fence it as untrusted data.

The implementation is an ordinary class; take `IPluginContext` in its constructor to know
which instance is providing it (its config, tenant) and to use contracts of its own. It is
never registered in DI — the runtime builds it per call behind a tracing, auditing proxy.

### Using one

Declare it in `Consumes`, then `ctx.Contracts.Get<T>()` (throws if no enabled provider) or
`TryGet<T>()` (null) — `Get` refuses anything not declared, even in-process. If the provider
allows several instances, set `BindingConfigKey` to the config field that names one (by id or
slug), and mark that field in the config schema:

```json
"rosterSlug": { "type": "string", "x-dcms-contract-binding": "roster.members@1" }
```

The admin renders it as a picker of enabled providers. A **required** consume makes enabling
the plugin fail (409) until a provider is enabled, and blocks disabling the last provider.

## Platform contracts

Declare these in `Consumes` like any other. Each acts **as the calling plugin**: tenant and
plugin id are stamped from its context and never taken from input.

| Contract | For |
|---|---|
| `dcms.storage@1` | Private JSON documents by collection/key, per instance or plugin-wide; optimistic versions; containment queries. Spares you a table, migration and RLS entry. |
| `dcms.secrets@1` | Credentials per instance, Vault-Transit-encrypted. **admin-api only** — spend them from an admin route, job or event handler. |
| `dcms.blobs@1` | Private files (≤10 MiB) under the tenant's prefix. User-visible files belong in media. |
| `dcms.cache@1` | Redis get/set/increment under the plugin's own prefix. |
| `dcms.email@1` | Transactional email via the queue; ≤50 recipients per call, 1000 per tenant+plugin per hour. |
| `dcms.notifications@1` | Admin-bell notifications, by permission. Pass `Kind` for a localised kind `plugin.{id}.{kind}` (add it to `NotificationKinds` and both locales). |
| `dcms.events@1` | Publish your contracts' events: `ctx.PublishAsync(new ReviewPosted(id), ct)`. |
| `dcms.jobs@1` | Enqueue one of your declared jobs, with delay and a dedupe key. |
| `dcms.content@1` | Published content of any instance in the tenant; resolve `ContentRef`s. |
| `dcms.search@1` | Full text over the tenant's published content. |
| `dcms.ai@1` | Completions from the tenant's own AI provider, under its quota. |

Event handlers (`IPluginEventHandler<T>`) and jobs (`IPluginJobHandler`) run in admin-api, in
their own scope, as the event's tenant, and only for tenants with an enabled instance of your
plugin. A failing handler is retried with backoff and dropped after five attempts; make them
idempotent.

## Fields the tenant defines

A content type can carry fields the plugin does not know about — Roster's `member` is the
reference case. Declare a `CustomFieldsDefinition` on the content type naming the
instance-config key holding the field *definitions* and the item field their *values* nest
under. The admin editor then renders one input per defined field. Values nest rather than
spread over the item so a tenant key can never shadow a plugin field. List the config key in
`publicConfigKeys` — a site cannot render fields whose labels and types it cannot read.
VisitorAuth's profile attributes follow the same idea for visitor accounts.

## Public instance config

Instance config is tenant-private. `publicConfigKeys` is an allow-list served by
`GET /api/{slug}/_config`, so a plugin that later gains a credential cannot leak it by
accident; `PublicConfigTests` pins which plugins expose anything.

## Your own tables

Prefer `dcms.storage`. A first-party plugin that genuinely needs relational data (VisitorAuth,
Forms) keeps its own schema: an EF migration, the DbContext registered in
`TenancyMigrator` (both `MigrateAllAsync` and `AssertRlsCoverage`), and every tenant table in
`RlsConfigurator.TenantTables`. Grants to the least-privilege role are schema-wide defaults
while RLS policies are opt-in, so a missing entry is a grant with nothing enforcing the tenant
predicate; `AssertCoverage` fails startup for a mapped `TenantId` table in neither list. Make
the context honour an enclosing `RlsScope.Tenant` in its query filter if plugin background
work reads it. Add the tables to the tenant purge in `TenantAdminEndpoints`.

## When a plugin needs a third-party credential

Credentials never go in instance config. Store them with `dcms.secrets@1` (or, as the
Meta plugins do, in a dedicated table under their own Transit key), keep only a handle in
config, and never list that handle in `publicConfigKeys`. A public service must not be able to
decrypt a tenant credential: content-api has no grant on the plugin-secrets key, so code that
spends one runs in admin-api. Bound what you pull from a third party in the fetch, not the
retention, and re-check every `maximum` in your schema server-side (the config validator
does). See `Dcms.AdminApi/Social/` and `docs/vault-secrets.md`.

## Tests

Manifest and contract shape tests go in `tests/Dcms.PluginSdk.Tests` (the registry is cheap to
build there); routes and data in `tests/Dcms.IntegrationTests` (`ContentFlowCollection` gives
you admin-api and content-api over real Postgres, Redis and NATS). `ContractDispatcherTests`
and `VisitorProfileTests` are the patterns for contract operations over HTTP.
