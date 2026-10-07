# Writing a DCMS plugin

A plugin is a .NET project implementing `IPlugin`. It owns its routes, its background work and
its data; it publishes an **API** — contracts other code can call, events it raises, hooks other
plugins may intercept, the content it keeps — in a separate `.Api` assembly; and it reaches
other plugins and the platform only through such contracts. The hosts (content-api, admin-api)
know no plugin by name.

- [ADR 0016](adr/0016-plugin-contracts.md): contracts, the dispatcher, platform contracts.
- [ADR 0017](adr/0017-plugin-api-ecosystem.md): `.Api` packages, open contracts, hooks,
  content events, installed plugins, the reference page.
- [ADR 0018](adr/0018-plugin-admin-pages.md): the plugin's page in the admin, and data sets.
- [ADR 0019](adr/0019-plugin-screens.md): plugins' own admin screens and menu entries,
  `@dcms/plugin-ui`, permissions plugins add.

Read a real one next to this page: **Blog** (a content plugin in a dozen lines), **Forms**
(routes, a hook, a consumer of VisitorAuth), **LiveChat** (host routes, its own events),
**Analytics** (site and admin planes, a contract for other plugins, an admin screen), and
`samples/Dcms.Plugins.Sample.Guestbook` — built as an outsider would, and using **every** part of
the system once, each with a comment saying what it is for. Start there.

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
- `MapHostEndpoints` routes are mapped as written, with no instance filter. When the request
  carries a workspace, the handler still gets the plugin's tenant-wide `IPluginContext` (no
  instance) and so its contracts; otherwise it resolves what it needs itself. If one sits at
  `/api/{literal}`, list the literal in `reservedSlugs` so no instance can be created with a
  slug it would shadow.
- Two plugins mapping the same method and pattern fail startup.
- Every route must say what gates it and every mutating route what it records; the coverage
  tests fail otherwise. With the SDK alone (any plugin, installed ones included):
  `.RequirePluginPermission("moderate")` (a bare action is the plugin's own permission; a full
  key like `"content:read"` works too), `.WithoutPermission("why")` for a public route,
  `.AuditAs("entry.approved")` (recorded as `plugin.{id}.entry.approved`, opened before the
  handler runs) or `.SkipAudit("why")`. The runtime turns them into the platform's own gate
  and audit metadata when it mounts the routes, and refuses a permission nobody declares.
  First-party plugins may use the platform's equivalents (`.RequirePermission`,
  `.RequireVisitor()`, `.AllowServicePrincipal`, `.PermissionExempt`, `.WithAudit`,
  `.AuditExempt`) directly.
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

### Offering actions to Dynamic Apps flows

A plugin can give tenant-built automations ([dynamic-apps.md](dynamic-apps.md)) something to
do by providing `automation.actions@1` (`IAutomationActionProvider` in
`Dcms.Plugins.DynamicApps.Api`):

```csharp
provides: [ContractProvision.Of<IAutomationActionProvider, MyActions>()],
```

- **`ListAsync`** returns descriptors: a name, a major version, a description, a risk, an
  input JSON Schema, and optionally a **permission**.
- **`ExecuteAsync`** runs one action for a flow step. It receives the rendered input, the
  step's idempotency key (the same on every retry), and the app instance and run ids.

Rules for providers:

- **Prefix action names** with your plugin id (`visitor-auth.set-attributes`). A name
  that clashes with a built-in action is ignored.
- **Never change what an action means** within a major version. Offer `@2` alongside `@1`.
- **Throw `ContractValidationException`** for a failure that the same input would repeat. The
  step then fails without retrying, while anything else is retried.
- **Set the permission** your plugin asks of an admin who does the same thing by hand.
  VisitorAuth's is `plugin:visitor-auth:manage`. Anyone who adds or changes a flow using the
  action must hold it, or the change does not validate and cannot publish. Without it, the
  right to publish an app would quietly include your plugin's rights.
- **Calls go through the contract runtime**, audited like any plugin-to-plugin call. Your
  plugin's own rules still apply: VisitorAuth refuses private attributes from a flow just as
  it does from any other caller.

A flow naming an action that no enabled provider offers fails validation, so it cannot
publish. Typed events reach flows by a different path: Dynamic Apps subscribes to
`visitor.registered` and `form.submitted` itself, and forwards each to the apps that have a
flow on it.

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

## Your page in the admin: data sets

Every instance has a page in the admin console (`/plugins/{slug}`): its wiring (what it offers,
what it uses and which instance answers, who relies on it), its data, settings, site routes,
developer reference and audit history. You write no UI for it. What you contribute is **data
sets**: tables of your data the console lists, searches, filters, pages, edits and acts on.

```csharp
dataSets: [DataSetDeclaration.Of<SubscribersDataSet>("subscribers", "Subscribers",
    readPermission: "plugin:newsletter:read", writePermission: "plugin:newsletter:manage")],

public sealed class SubscribersDataSet(IPluginContext context, MyDbContext db) : IPluginDataSet
{
    public Task<DataSetSchema> DescribeAsync(CancellationToken ct) => Task.FromResult(new DataSetSchema(
        Columns: [new("email", "Email", DataColumnKinds.Email, Sortable: true, Primary: true),
                  new("confirmed", "Confirmed", DataColumnKinds.Boolean)],
        ItemSchema: new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
            ["name"] = new JsonObject { ["type"] = "string", ["title"] = "Name" } } },
        Filters: [new("confirmed", "Status", [new("yes", "Confirmed"), new("no", "Pending")])],
        Actions: [new DataAction("resend", "Resend confirmation", OpRisk.Safe)],
        Searchable: true, CanUpdate: true, CanDelete: true, DefaultSort: "email"));

    public Task<DataPage> ListAsync(DataQuery query, CancellationToken ct) => ...; // query.Search, .Filter("confirmed"), .Sort, .Skip
    public Task<DataRow?> GetAsync(string key, CancellationToken ct) => ...;
    public Task<DataRow?> UpdateAsync(string key, JsonObject values, CancellationToken ct) => ...;
    public Task<bool> DeleteAsync(string key, CancellationToken ct) => ...;
    public Task<DataActionResult> RunActionAsync(string action, IReadOnlyList<string> keys, JsonObject? input, CancellationToken ct) => ...;
}
```

- Permissions: a bare action (`"read"`) is the plugin's own, or name a full key.
- **The runtime is the gate, not you.** Only what the schema says is called (`CanUpdate`,
  `CanDelete`, `CanCreate`, `CanDownload`, declared actions). Values reach `Create`/`Update`
  cut down to `ItemSchema`'s properties and validated against it; filters arrive only with
  declared options, sorts only on sortable columns, pages capped at 100. Throw
  `ContractValidationException` (400) or `ContractConflictException` (409) for the rest.
- **Permissions** are full keys, your own or a platform one (`content:read`); unset means
  `plugins:manage`. A set the caller cannot read is not listed; write needs the write key.
- **Audit** is the runtime's: every change is `plugin.data.*` against the instance, naming the
  set, row and action. (The source scan still asks a file using `ExecuteUpdate`/`ExecuteDelete`
  to say how it is audited — say so in the class comment.)
- The set is built per request with your `IPluginContext` for the instance being viewed, like a
  route handler. Describe may read instance config (VisitorAuth's columns follow its attributes).
- Row `Values` are by column key; a nested object renders as a definition list and multiline
  text as a transcript. Column kinds: `text`, `number`, `boolean`, `datetime`, `email`, `url`,
  `badge`, `bytes`, `json`, `media`. `"format": "json"` in an item schema gets a JSON editor.
- **Free sets.** A plugin consuming `dcms.storage` gets "Stored data" (every document, this
  instance's and plugin-wide, editable as JSON); one consuming `dcms.blobs` gets "Files"
  (download, delete). An installed plugin has a data view without writing any of the above.

Served at `/api/admin/plugins/{slug}/_data` (list sets with schemas), `…/_data/{set}` (query:
`search`, `sort`, `desc`, `page`, `pageSize`, `f.{filter}`), `…/{set}/row?key=` (GET, PUT,
DELETE), `…/{set}/rows` (POST), `…/{set}/actions/{action}` (`{ keys, input }`),
`…/{set}/download?key=`. A switched-off instance's data stays browsable and editable: it is
hidden from sites, not from its owners.

## Permissions a plugin adds

```csharp
permissions:
[
    new PermissionDefinition("read", "View guestbooks", "See entries and counts of every guestbook.", GrantToMembers: true),
    new PermissionDefinition("moderate", "Moderate guestbooks", "Approve, reject, edit, reply to and delete entries."),
],
```

The key is `plugin:{pluginId}:{action}` (actions are unique kebab-case). The role editor lists
them under the plugin with their description; Owners hold every plugin permission (a startup
backfill adds new ones); `GrantToMembers` gives the built-in Member role the permission once,
when a tenant adds its first instance of the plugin — a default the tenant can take away. Use
them anywhere a permission is named — routes (`RequirePluginPermission`), data sets, admin
screens, contract operations (`[Operation(Permission = "plugin:…")]`, a full key there) — and
in screens with `useCan("moderate")`.

## Admin screens and menu entries

A plugin draws its own screens in the admin console: React components, rendered inside the
console with its theme, components, data cache, permissions and language. The manifest
**declares** them — so the platform draws the menu and guards the route without running plugin
code — and the plugin's admin UI module **implements** them:

```csharp
adminScreens:
[
    // One page for the tenant, /app/{pluginId}/{id}, with a menu entry.
    new AdminScreen("overview", "Guestbooks", AdminScreenScope.Plugin, Permission: "read", IconName: "BookOpen",
        Nav: new AdminNavPlacement("main", 60), Titles: new Dictionary<string, string> { ["cs"] = "Knihy návštěv" }),
    // A tab on each instance's page, /plugins/{slug}/{id}.
    new AdminScreen("moderation", "Moderation", AdminScreenScope.Instance, Permission: "moderate"),
],
```

```tsx
// admin/src/index.tsx
import { definePluginAdmin } from '@dcms/plugin-ui';
export default definePluginAdmin({
  screens: { overview: OverviewScreen, moderation: ModerationScreen },
  configWidgets: { 'guestbook-accent': AccentWidget },   // for "format": "guestbook-accent" in schemas
  locales: { en, cs },                                    // read with usePluginT()
});
```

- **Menu entries appear only while an instance of the plugin is enabled**, in the section and
  order `Nav` names (`main`, `build` or `admin`; platform entries use orders 0–99), titled in the
  reader's language. A switched-off plugin's plugin-wide screen says so and links to its page;
  its instance screens stay reachable from that page.
- A screen renders behind the permission its manifest names (default `plugins:manage`) and in its
  own error boundary; a plugin that throws does not take the console down.
- Everything a screen needs from the console comes through `@dcms/plugin-ui`, never console
  imports: `usePluginApi()` (the signed-in admin API client), `instancePath(slug, path)` for
  the instance's own admin routes, `useCan(permission)`, `usePluginT()` (the plugin's
  `locales`, falling back to the console's words), `usePluginScreen()` (plugin, instance,
  instances), `usePluginHost()` (auth headers and the content API base for a hub,
  `navigate`, and `components.DataSet` to embed one of its data sets). Use `@dcms/ui` for
  components and `toast`.
- **Built-in plugins** keep their UI beside their C#: `src/Plugins/{Assembly}/admin`, a pnpm
  package whose `src/index.tsx` is the module. The console compiles every such folder in,
  lazily, by folder name — adding a plugin with a UI is adding the folder. Forms (inbox),
  AI Chatbot (chat console), Analytics (dashboard) and Instagram/Facebook (the Meta account
  widget) are the examples.
- **Installed plugins** ship `admin/index.js` (and optionally `admin/index.css`) in their folder,
  built with the SDK's preset, which leaves React, `@dcms/ui`, react-query, i18n and
  `@dcms/plugin-ui` to the console's copies:

  ```ts
  // vite.config.ts
  import { defineConfig } from 'vite';
  import { dcmsPluginAdmin } from '@dcms/plugin-ui/vite';
  export default defineConfig(dcmsPluginAdmin({ entry: 'src/index.tsx' }));
  ```

  Ship plain CSS on the console's tokens (`hsl(var(--card))`, `var(--radius)`), prefixed with
  your own namespace; Tailwind classes the console happens not to use do not exist. The csproj
  publishes `admin/dist` as `admin/` (see the Guestbook's). The files are served anonymously
  from `/api/admin/plugin-ui/assets/{pluginId}/…` — they are code, the same for every tenant.

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
`dataSets` and `adminScreens` (above).

Operator settings bind like any options: `host.SettingsFor(id).Bind(options)` in
`ConfigureServices`, then register the object (the Guestbook's `GuestbookOptions`).

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

`samples/Dcms.Plugins.Sample.Guestbook` (with `.Api` beside it) is a complete one, using every
feature on this page: its own contract with site, admin and AI operations, events and a hook;
interceptors of its own hook and of `forms.submitting`; handlers of its own event and of
`blog.post.published`; an interval job and an on-demand one; storage, files, cache,
notifications and email; permissions with a default grant; a content type; a data set with
input and dangerous actions; site, admin and host routes; an OpenAPI fragment; and an admin UI
with a plugin-wide screen, an instance screen and a config widget, built with the Vite preset.
The integration tests install it from a folder and drive it on the real hosts. The admin's **API reference** page
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
