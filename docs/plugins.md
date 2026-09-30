# Writing a DCMS plugin

A plugin is a .NET project implementing `IPlugin`. It owns its routes, its background work and
its data; it publishes an **API** — contracts other code can call, events it raises, hooks other
plugins may intercept, the content it keeps — in a separate `.Api` assembly; and it reaches
other plugins and the platform only through such contracts. The hosts (content-api, admin-api)
know no plugin by name.

- [ADR 0016](adr/0016-plugin-contracts.md): contracts, the dispatcher, platform contracts.
- [ADR 0017](adr/0017-plugin-api-ecosystem.md): `.Api` packages, open contracts, hooks,
  content events, installed plugins, the reference page.

Read a real one next to this page: **Blog** (a content plugin in a dozen lines), **Forms**
(routes, a hook, a consumer of VisitorAuth), **LiveChat** (host routes, its own events),
**Analytics** (site and admin planes, a contract for other plugins), and
`samples/Dcms.Plugins.Sample.Greeter` (built as an outsider would).

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
            ContractRequirement.Of<IPluginStorage>(),                 // platform: always present
            ContractRequirement.Of<IVisitorIdentity>(optional: true),  // another plugin's
        ],
        subscribes: [EventSubscription.Of<VisitorRegistered, WelcomeHandler>()],
        intercepts: [HookSubscription.Of<FormSubmitting, SpamFilter>(priority: 10)],
        jobs: [JobDeclaration.Of<DigestJob>("digest", TimeSpan.FromHours(24))]);

    public void MapEndpoints(IPluginEndpointBuilder endpoints) =>
        endpoints.MapPost("/reviews", async (ReviewInput body, IPluginContext ctx, CancellationToken ct) =>
        {
            await ctx.Contracts.Get<IPluginStorage>().PutAsync(/* … */, ct);
            return Results.Accepted();
        }).PermissionExempt("Anonymous by design: any visitor may review.")
          .WithAudit(AuditActions.ForPlugin("reviews", "submitted"), "review");
}
```

Only `Manifest` is required. Every other `IPlugin` member has a default:

| Member | Default | Override to |
|---|---|---|
| `ConfigureServices(services, host)` | nothing | register services; `host.Plane` says which host (site or admin), `host.SettingsFor(id)` is the operator's `Plugins:{id}` section |
| `MapEndpoints` | list + get-by-slug for every content type | map site routes under `/api/{slug}/…` |
| `MapAdminEndpoints` | nothing | map admin routes under `/api/admin/plugins/{slug}/…` |
| `MapHostEndpoints(app, host)` | nothing | map routes that belong to no instance (a SignalR hub, an OAuth callback, a tenant-wide dashboard) |
| `BuildOpenApiFragment` | list + get for every content type | document bespoke site routes |

Built-in plugins are registered in `DcmsPluginSet.AddAll` (`src/Plugins/Dcms.Plugins.All`);
installed ones are loaded from the plugin directory (below). Both hosts build their registry
the same way, and it refuses to start on a manifest that does not add up.

## The .Api assembly

Everything another plugin needs to integrate with yours goes in `Dcms.Plugins.{Name}.Api`:
contract interfaces, their input/output records, event records, hook records, and permission
constants. It references **`Dcms.PluginSdk.Abstractions` and nothing else** (a test enforces
it), because it is what consumers compile against and what ships as a NuGet package — a data
layer in it would become every consumer's dependency.

```
src/Plugins/Dcms.Plugins.Reviews.Api/     IReviews, ReviewPage, ReviewPosted, ReviewPermissions
src/Plugins/Dcms.Plugins.Reviews/         ReviewsPlugin, Reviews (the implementation), routes, jobs
```

A consumer references the `.Api` — Forms references `Dcms.Plugins.VisitorAuth.Api`, never
`Dcms.Plugins.VisitorAuth`.

## Routes

- `MapEndpoints` routes mount under `/api/{instanceSlug}/…` in **content-api** (the public
  plane); `MapAdminEndpoints` under `/api/admin/plugins/{instanceSlug}/…` in **admin-api**.
  A request reaches the handler only for an **enabled instance of this plugin**; anything else
  is a 404. Take `IPluginContext` as a handler parameter: tenant, instance (with its config),
  actor, contracts and hooks.
- `MapHostEndpoints` routes are mapped as written, with no instance filter: the handler
  resolves tenant and instance itself. If one sits at `/api/{literal}`, list the literal in
  `reservedSlugs` so no instance can be created with a slug it would shadow.
- Two plugins mapping the same method and pattern fail startup.
- Every mutating route must say what gates it — `.RequirePermission(...)`,
  `.RequireVisitor()` (VisitorAuth), `.AllowServicePrincipal(scope, why)` or
  `.PermissionExempt("why")` — and what it records — `.WithAudit(...)` or `.AuditExempt("why")`.
  The coverage tests fail otherwise.
- A route that browsers on other origins call registers its own anonymous CORS policy in
  `ConfigureServices` (site plane only) and names it with `.RequireCors(...)`.
- A route served by a hand-written helper of the site runtime (`@dcms/api-client`) declares
  itself in `clientBindings`, so the generated TypeScript client has a typed member for it.

## Contracts

### Offering one

```csharp
[DcmsContract("reviews", 1, Description = "Product reviews.",
    Events = [typeof(ReviewPosted)], Hooks = [typeof(ReviewSubmitting)])]
public interface IReviews
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Ai,
        ReturnsExternalText = true, Description = "Approved reviews of a product.")]
    Task<ReviewPage> ListAsync(ReviewQuery input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = ReviewPermissions.Moderate, Expose = OpExposure.Admin,
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
  time (`roster.members` went to `@2` when its results became typed). Event and hook names are
  dotted kebab-case too.
- `Expose` decides who outside the plugin runtime may call the operation (it is always
  callable plugin-to-plugin by a declared consumer):
  - `Site` — the public site, at `POST /api/{slug}/_contracts/{id}/{op}`, and the tenant's
    generated client (`api.{slug}.{contract}.{op}()`). An operation with a `Permission` is
    never served on this plane.
  - `Admin` — members, at `POST /api/admin/contracts/{id}/{op}`, subject to `Permission`.
    Owners hold every plugin's permissions automatically; other roles get them in the role editor.
  - `Ai` — the admin assistant as a tool once the tenant opts the instance in; the site chatbot
    too for Read operations also exposed to `Site`.
- `ReturnsExternalText` marks results containing text written by people with no access
  (visitors). AI surfaces fence it as untrusted data.

The implementation is an ordinary class; take `IPluginContext` in its constructor to know which
instance is providing it and to use contracts of its own. It is never registered in DI — the
runtime builds it per call behind a tracing, auditing proxy.

### Several providers: open contracts

A contract belongs to its interface, not to one plugin. Several plugins may implement the same
one — two payment gateways, two map providers — each listing it in `provides`. They must all
implement the *same* interface (reference the one `.Api` declaring it); two interfaces claiming
one id fail startup. A consumer then:

- gets the **bound** instance when its `BindingConfigKey` names one (by id or slug), or the
  **only** enabled instance across all providers, with `Get<T>()`;
- gets **all** of them with `GetAll<T>()` — the extension-point pattern: a sitemap built from
  every `ISitemapSource`, a checkout offering every `IPaymentGateway`.

A required consume is satisfied by an enabled instance of *any* provider.

### Using one

Declare it in `Consumes`, then `ctx.Contracts.Get<T>()` (throws if no enabled provider),
`TryGet<T>()` (null) or `GetAll<T>()`. All three refuse a contract not declared, even
in-process. For a multi-instance provider, set `BindingConfigKey` and mark the config field:

```json
"rosterSlug": { "type": "string", "x-dcms-contract-binding": "roster.members@2" }
```

The admin renders it as a picker of enabled providers. A **required** consume makes enabling
the plugin fail (409) until a provider is enabled, and blocks disabling the last provider.

## Events and hooks

**Events** say something happened. The provider publishes with `ctx.PublishAsync(new
ReviewPosted(id), ct)` (through `dcms.events@1`); every plugin subscribed to it
(`subscribes: [EventSubscription.Of<ReviewPosted, Handler>()]`, and consuming the contract)
gets it later, off the request, in admin-api, once per tenant with an enabled instance of the
subscriber. A provider may handle its own events without consuming itself — LiveChat raises the
agents' notification from its own `live-chat.conversation.started`. Handlers are retried with
backoff and dropped after five attempts; make them idempotent.

**Hooks** ask before something happens. The provider declares hook records on the contract
(`Hooks = [typeof(FormSubmitting)]`, records implementing `IPluginHook` with
`[ContractHook("forms.submitting")]`) and runs them in-process:

```csharp
var outcome = await ctx.Hooks.RunAsync(new FormSubmitting(instanceId, form, data, visitorId), ct);
if (outcome.Cancelled) return Results.BadRequest(new { error = outcome.Reason });
data = outcome.Value.Data;   // interceptors may have rewritten it
```

Interceptors (`intercepts: [HookSubscription.Of<FormSubmitting, SpamFilter>(priority)]`)
implement `IPluginHookHandler<T>` and return `HookResult.Continue(hook with { … })` or
`HookResult.Cancel(hook, reason)`. They run highest priority first, only for plugins enabled in
the tenant; the first cancel ends the chain. Hooks are **fail-open**: an interceptor that throws
or overruns `Plugins:HookTimeoutMs` (2 s) is logged and skipped. Only a provider of the contract
may run its hooks, and it decides what to keep of the result — Forms re-filters rewritten data to
the form's declared fields.

## Content plugins

A plugin whose data is content (Blog, Events, galleries…) declares content types and gets the
editor, the delivery API, search and the builder palette for free. To offer that data as an API:

```csharp
// .Api
public sealed record BlogPost(string Title, string? Excerpt, string Body, Guid? CoverImage, IReadOnlyList<string>? Tags);

[ContractEvent("blog.post.published")]
public sealed record BlogPostPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[DcmsContract("blog.posts", 1, Events = [typeof(BlogPostPublished), typeof(BlogPostUnpublished)])]
public interface IBlogPosts
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai)]
    Task<ContentPage<BlogPost>> ListAsync(ContentPageRequest input, CancellationToken ct);
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai)]
    Task<ContentEntry<BlogPost>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}

// plugin
internal sealed class BlogPostsSource(IPluginContext context)
    : PublishedContentSource<BlogPost>(context, "post"), IBlogPosts { /* two one-line forwards */ }

new ContentTypeDefinition("post", fields, …,
    Published: typeof(BlogPostPublished), Unpublished: typeof(BlogPostUnpublished))
```

`PublishedContentSource<T>` reads published items through `dcms.content@1` (consume it) and maps
their data to `T`, tolerating a cleared media reference (`""`). The platform raises the declared
lifecycle events for **every** publish path — the editor, a schedule, an import — by bridging
`content.published`/`content.unpublished`.

## Platform contracts

Declare these in `Consumes` like any other. Each acts **as the calling plugin**: tenant and
plugin id are stamped from its context and never taken from input.

| Contract | For |
|---|---|
| `dcms.storage@1` | Private JSON documents by collection/key, per instance or plugin-wide; optimistic versions; containment queries. Spares you a table, migration and RLS entry. |
| `dcms.secrets@1` | Credentials per instance, Vault-Transit-encrypted. **Admin plane only** — spend them from an admin route, job or event handler. |
| `dcms.blobs@1` | Private files (≤10 MiB) under the tenant's prefix. User-visible files belong in media. |
| `dcms.media@1` | Resolve a media asset id to its URLs; **import** a file from a public https URL (internal addresses refused, redirects included) into the tenant's library through the upload pipeline (admin plane only). |
| `dcms.cache@1` | Redis get/set/increment under the plugin's own prefix. |
| `dcms.email@1` | Transactional email via the queue; ≤50 recipients per call, 1000 per tenant+plugin per hour. |
| `dcms.notifications@1` | Admin-bell notifications, by permission. Pass `Kind` for a localised kind `plugin.{id}.{kind}` (add it to `NotificationKinds` and both locales). |
| `dcms.events@1` | Publish your contracts' events: `ctx.PublishAsync(event, ct)`. |
| `dcms.jobs@1` | Enqueue one of your declared jobs, with delay and a dedupe key. |
| `dcms.content@1` | Published content of any instance in the tenant; resolve `ContentRef`s. |
| `dcms.search@1` | Full text over the tenant's published content. |
| `dcms.ai@1` | Completions from the tenant's own AI provider, under its quota. |

A host may implement a platform contract differently from the default (admin-api's media can
import; content-api's cannot): the first registration of a contract wins.

## Configuration

Two levels, never mixed:

- **Instance config** — per tenant, per instance, edited in the admin against your
  `configJsonSchema` and validated server-side. Tenant-private: only `publicConfigKeys` are
  served to sites (`GET /api/{slug}/_config`), so a plugin that later gains a credential cannot
  leak it by accident.
- **Operator settings** — per deployment, `Plugins:{id}:*` in the host's configuration (env
  `Plugins__reviews__ApiBase`), read in `ConfigureServices` through `host.SettingsFor(id)`.

Platform-wide switches: `Plugins:Disabled` (list of ids) removes a plugin from the deployment
without uninstalling it — anything requiring its contracts then fails startup naming both;
`Plugins:HookTimeoutMs`; `Plugins:Directory` (below).

Manifest flags the platform reads instead of knowing your plugin: `tracksVisitors` (a published
site asks for cookie consent while an instance is enabled), `reservedSlugs`, `clientBindings`,
`nav` (a sidebar entry).

## Building a plugin outside this repo

The SDK and every plugin's `.Api` are published to the project's GitLab package registry by CI
(`scripts/ci/pack-plugin-sdk.sh`), versioned `major.minor.<pipeline>`:

```xml
<!-- nuget.config -->
<packageSources>
  <add key="dcms" value="https://gitlab.highgeek.eu/api/v4/projects/<project-id>/packages/nuget/index.json" />
</packageSources>
```

```xml
<!-- YourPlugin.csproj -->
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
  <PackageReference Include="Dcms.PluginSdk.Abstractions" Version="1.0.*" />
  <PackageReference Include="Dcms.Plugins.Blog.Api" Version="1.0.*" />   <!-- whatever you integrate with -->
</ItemGroup>
```

`samples/Dcms.Plugins.Sample.Greeter` is a complete one: its own contract and site route, a
subscription to `blog.post.published` counting posts in `dcms.storage`, and a
`forms.submitting` interceptor refusing spam. The admin's **API reference** page
(Marketplace → a plugin → API reference) shows any plugin's contracts, events, hooks, data and
configuration with C# to start from, and runs its admin operations against an instance.

## Installing a plugin

Publish it (`dotnet publish -c Release -o plugins/YourPlugin`) and put the folder in the plugin
directory — `./plugins` beside the compose files, mounted read-only at `/app/plugins` in
content-api and admin-api (`Plugins:Directory`) — then restart both. The loader:

- takes each subfolder as one plugin; its main assembly is the one with a `.deps.json`;
- gives it its own `AssemblyLoadContext`, but resolves anything the host already has (the SDK,
  the built-in plugins' `.Api` assemblies, the framework) to the host's copy, so contract,
  event and hook types are the same on both sides — reference packages of the versions the
  host runs;
- refuses a plugin built against another SDK **major**, and stops startup on any folder it
  cannot load, naming it.

An installed plugin runs in-process with the host's rights. Install only code you trust;
tenants cannot install anything. The marketplace lists it with `source: installed`.

## Fields the tenant defines

A content type can carry fields the plugin does not know about — Roster's `member` is the
reference case. Declare a `CustomFieldsDefinition` on the content type naming the
instance-config key holding the field *definitions* and the item field their *values* nest
under. The admin editor then renders one input per defined field. Values nest rather than
spread over the item so a tenant key can never shadow a plugin field. List the config key in
`publicConfigKeys` — a site cannot render fields whose labels and types it cannot read.
VisitorAuth's profile attributes follow the same idea for visitor accounts.

## Your own tables

Prefer `dcms.storage`. A first-party plugin that genuinely needs relational data (VisitorAuth,
Forms, Analytics, LiveChat, the Meta feeds) keeps its own schema in `Dcms.Shared.Data`: an EF
migration, the DbContext registered in `TenancyMigrator` (both `MigrateAllAsync` and
`AssertRlsCoverage`), and every tenant table in `RlsConfigurator.TenantTables`. Grants to the
least-privilege role are schema-wide defaults while RLS policies are opt-in, so a missing entry
is a grant with nothing enforcing the tenant predicate; `AssertCoverage` fails startup for a
mapped `TenantId` table in neither list. Make the context honour an enclosing
`RlsScope.Tenant` in its query filter if plugin background work reads it. Add the tables to the
tenant purge in `TenantAdminEndpoints`. An installed (out-of-repo) plugin has no migrations:
`dcms.storage` is its database.

## When a plugin needs a third-party credential

Credentials never go in instance config. Store them with `dcms.secrets@1` (or, as the Meta
plugins do, in a dedicated table under their own Transit key), keep only a handle in config,
and never list that handle in `publicConfigKeys`. A public service must not be able to decrypt
a tenant credential: content-api has no grant on the plugin-secrets key, so code that spends one
runs on the admin plane — Instagram stories are fetched through an admin-plane route that
content-api calls with a service token (`AllowServicePrincipal`). Bound what you pull from a
third party in the fetch, not the retention, and re-check every `maximum` in your schema
server-side. See `Dcms.Plugins.Meta.Core` and `docs/vault-secrets.md`.

## Tests

Manifest, contract, hook and `.Api` shape tests go in `tests/Dcms.PluginSdk.Tests` (the
registry is cheap to build there; `PluginLoaderTests` installs the sample from a folder);
routes and data in `tests/Dcms.IntegrationTests` (`ContentFlowCollection` gives you admin-api
and content-api over real Postgres, Redis and NATS). `ContractDispatcherTests`,
`ContentEventBridgeTests`, `PluginOwnedRoutesTests` and `LiveChatPluginTests` are the patterns
for contract operations, content events, plugin routes and plugin events.
`HostIndependenceTests` keeps the services free of plugin references.
