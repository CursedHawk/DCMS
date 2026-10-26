# The Mode D visual React builder

Mode D sites (`SiteRenderMode.ReactBuilder`) are React single-page apps composed in a
drag-and-drop builder. Pages are **trees of components stored as JSON in a git
repository**, and the published site is a Vite build that renders those trees with the
same component code the canvas uses. See [ADR 0020](adr/0020-mode-d-react-visual-builder.md)
for the decisions; Mode A ([mode-a-builder.md](mode-a-builder.md)) stays, unchanged.

## What a site is

```
dcms/app.json                      routes, menus, the app shell, SEO defaults
dcms/theme.json                    design tokens (a design kit)
dcms/pages/<id>.json               one page: { schemaVersion, id, title, seo?, data?, root }
dcms/components/<name>/v<N>.json   a component the tenant built, one file per version
dcms/code/<name>.json              a developer component's contract
src/components/<name>.tsx          its source
src/main.tsx                       GENERATED: globs dcms/**/*.json into <DcmsApp/>
src/dcms/runtime/**                GENERATED: @dcms/site-runtime, vendored per site
src/api/**                         GENERATED: the tenant API client (as in Mode B)
package.json, vite.config.ts, …    the scaffold (template `visual`)
```

A node is `{ id, type, props?, slots?: { name: Node[] }, responsive?, action?, bind?,
version? }`. Nothing in `dcms/` is GrapesJS's: the canvas maps one document to GrapesJS
and back on every edit, and a node it does not know is kept verbatim.

`src/dcms/runtime` and `src/api` are regenerated layers (manifest + fingerprint), served by
`GET /api/admin/site-runtime` and the API layer; the builder offers an update when either
is stale. Editing them by hand is overwritten.

## One runtime, two places

`packages/site-runtime` (`@dcms/site-runtime`) holds the schemas, the component registry,
the built-in components, the renderer, the data client, actions, head management and the
validator. It may import only itself, react, react-dom, react-router, zod and dompurify —
it is copied into every site.

- **Canvas**: every node is drawn by its own React root inside the GrapesJS frame, in
  `RenderMode` `edit` (links have no href, actions do nothing). Each node renders in a
  `.dcms-node` wrapper, each slot is a real `.dcms-slot` element; GrapesJS owns the
  wrappers, React owns what is inside them.
- **Preview** and **published site**: `siteRoutes` under a router, `RenderMode` `live`.
  The preview is the published app under a memory router, in its own frame.

The canvas frame is **same-origin with the admin**, so only DCMS's own runtime code ever
runs in it. Tenant components are data the runtime interprets; rich text goes through
DOMPurify. Developer-written TSX never runs there: it is drawn in sandboxed island frames —
see *Developer components*.

## Editing surfaces

- **Design / Split / Code / Preview** — canvas, canvas + Monaco over `dcms/**/*.json`,
  Monaco alone, and the running site. All edit one working draft that autosaves.
- **Rail**: Components (palette), My components, Layers, Pages, Theme, Git, Deployments,
  Problems, Agent.
- **Devices**: Desktop, Tablet (≤1024px), Mobile (≤640px). On Tablet/Mobile an edit to a
  `responsive` prop is an override for that size and smaller, stored in `node.responsive`
  and emitted as `t-`/`m-` classes.

## Placement

One rule, `canPlace(registry, parentType, slot, childType, siblings)`, decides what may go
where: GrapesJS's drop targets, the validator and the agent's tools all call it, so the
three can never disagree. A component with empty `allowedParents` (the page itself) cannot
be placed at all.

## Pages, routes and the shell

`app.json.routes` maps paths to pages; `:param` segments make detail routes. The shell
(`app.json.shell`) is a tree with exactly one `dcms.outlet` where every page renders — the
header menu and footer live there. Menus (`navigation.main`, …) are drawn by `dcms.nav`.

The Vite plugin `dcmsRoutes` writes one HTML file per route with its head tags
(`about.html`, `events_@.html` for `/events/:slug`); site-host serves those before the SPA
fallback, so deep links and link previews work.

Routes without parameters are also **prerendered**: the template's build runs a second,
SSR pass over `src/entry-server.tsx` and `scripts/prerender.mjs` fills each route's
`<div id="root">` (`runtime/server.tsx`); `src/main.tsx` hydrates what it finds. Plugin
content is still fetched in the browser — the build cannot reach the API — so collections
prerender in their loading state, as the browser's first render does, and rich text is
sanitised client-side (empty box on the server). Prerendering is best effort: a route that
fails keeps its empty root and never fails the build.

## Components the tenant builds

A selection becomes a component (`dcms/components/<name>/v1.json`): a template tree plus
the settings (`props` → `bindings` onto inner nodes) and slots (`slotTargets` onto an
**empty** inner slot) it exposes. Instances pin a `version`. A version that pages use is
never edited in place: editing starts `v<N+1>`, and *Update to vN* migrates an instance,
dropping (and naming) settings the new version no longer has. The instance's notice lists
what updating changes first: settings that go (with their value), settings that arrive, and
slots whose content stops showing because the new version has no such slot (the content stays
in the file).

## Data

- **Collection** (`dcms.collection`, `props.source { instance, contentType }`) repeats its
  `item` slot per item; `empty` and `error` are slots too. *More than fit* (`paging`) adds a
  “Load more” button (a growing page, up to 100 items) or page numbers (the delivery API's
  `page`); a connection pages over its snapshot without another request.
- **Detail page**: `page.data { source, param }` reads the item the route names; a missing
  item is a 404.
- **Bindings**: `node.bind` maps a prop to a field path — `title`, `values.colour`, or
  `#slug`/`#id`/`#index`/`#number`/`#count`. `BINDABLE` decides which field kinds a prop
  kind accepts. A list of media (an event's `photos`) binds to an image and shows its first.
- **Actions**: navigate (`/events/:slug` fills from the item), open-external, scroll-to,
  submit-form, open-modal, show-toast.
- **Forms**: `dcms.form` posts its fields to the Forms plugin.
- On the canvas, data comes from the **preview proxy** (`/api/admin/sites/{id}/preview/api/*`)
  — the environment's live content and data (ADR 0026). Published sites read same-origin `/api/*`.

`checkVisualSite` checks sources and bound fields against the tenant's content types, so a
renamed field or a removed plugin shows up in Problems, not as an empty card.

## External API connections

*Settings › Connections* connects an external HTTP API — a ticket shop, a booking system — for
sites to show its data. **Synced, never proxied:**

- A connection is a base URL (https), a key (bearer, a named header or a query parameter), the
  GET operations a site may use (`/events`, `/events?city=brno`, at most ten) and a refresh
  interval (15 min – 24 h). Stored in `cms.api_connections`; the key is Transit-encrypted under
  `dcms-api-connections`, which **only admin-api** may use, and no response ever carries it.
- admin-api calls each operation (public addresses only, no redirects, 15 s, 512 KB, JSON only)
  on save, on *Refresh now*, and on schedule (`ApiConnectionWorker`), and stores the response in
  `cms.api_snapshots` with the shape it found — where the list sits and an item's fields. A
  failing operation keeps its last good snapshot and reports the error on the connection.
- content-api serves `GET /api/connections/{slug}{operation}` from the snapshot. It holds no key
  and makes no outbound call, so visitor traffic never reaches the provider or its quota, and an
  operation that is not listed does not exist.
- On a site, a source is `{ connection, operation, items?, id?, slug? }`: a Collection lists the
  items at `items`; a detail page finds the item whose `slug` field (default `slug`, else `id`)
  matches the route. Field kinds in the picker are guessed from names (image, url, date); any
  field may be bound, since an API's items are whatever it returns. The agent reads them with
  `list_connections`.

## Dynamic Apps records

A Dynamic Apps application's tables (a CRM's accounts, contacts, deals) are records, not plugin
content: content delivery owns `/api/{slug}/{contentType}`, an app serves
`/api/{app}/data/{table}` under its public access rules (ADR 0021).

- **Source** `{ app, table, sort?, expand?, related? }`. A Collection lists records, a detail page
  (`/deals/:id`) shows one, bindings read field API names. A lookup is an id unless `expand`
  names it, then `account.name` reads. `related` (a relationship of `table`) lists the records
  related to the one in scope — an account's deals on the account's page — from
  `/data/{table}/{id}/{relationship}`. A record's `#slug` and `#id` are both its id.
- **`dcms.data-table`** — search, sort, filters, columns and saved views (site-table-views);
  `rowLink` (`/deals/:id`) makes each row's first cell a link to its record.
- **`dcms.record-form`** — adds a record, or (mode `edit`, on that table's detail page) PATCHes the
  page's record with the version it read, so a concurrent change is refused. Fields come from the
  app's public model; `afterSave` navigates (`:id` filled). It never saves on the canvas.
- **Catalog.** The builder reads each app's public model through the site's preview proxy
  (`/api/admin/sites/{id}/preview/api/{app}/_model`) — exactly what the site may read, without a
  plugin permission. Picked in the panel, a table's lookups come expanded.
- **Checks.** `checkVisualSite` names a table the site cannot read (absent, unpublished, closed to
  the public), a binding through an unexpanded lookup, a data table's unknown column or a rowLink
  that is not a site path, and related records outside a record of their table.

## Signing in

Two plugins sign people in (see [user-auth.md](user-auth.md)); the runtime's `accounts.ts` keeps
who is signed in with each, and three components draw it. Each takes `accounts`: `users` or
`visitors`.

- **User Authentication** (`users`) signs in at the edge: `dcms.sign-in` is a button to
  `/.edge/site/signin?returnUrl=` (`afterSignIn`, else the page); who it is comes from the edge's
  `/me`, sign-out is `/.edge/site/signout`. Both go through `SiteEdge.signIn`/`signOut`, which the
  builder's preview points at its popup. No password form and no registration — ever.
- **Visitor Authentication** (`visitors`): `dcms.sign-in` with `instance` and `mode`
  (`sign-in`|`register`) is the email and password form, posting `/api/{instance}/login` or
  `/register`. The tokens are kept in `localStorage` (`dcms.visitor`); the site's data client
  sends the access token as a bearer on every `/api` call and renews it with the refresh token
  once its `exp` is near, so Forms and Dynamic Apps (public access `own`) see the visitor. A
  refused renewal signs out.
- **`dcms.signed-in`** (`show`: `signed-in`|`signed-out`) draws its slot only on that side, and
  nothing until it is known; **`dcms.account`** is the name (`greeting` with `{name}`) and Sign
  out. Both are cosmetic: pages are protected by the edge's rules, data by API access.
- Signing in or out runs `resetSiteAccess`, which also drops the data cache and makes mounted
  `useData` readers and data tables fetch again — one person's records are never the next one's.
- **Preview.** The toolbar's User Authentication menu (Sign in, View as, Sign out) serves the page's
  buttons too. A visitor's token cannot ride `Authorization` through the console (it is the
  member's), so the preview's data client sends `X-Dcms-Visitor-Token` and the preview proxy
  forwards it as the bearer to content-api. Preview visitors are real visitors of the environment
  (ADR 0026). The visitor menu shows and signs out the visitor. No View as for visitors.
- **Checks.** A Forms form with a password field is an error (it would store the password in the
  inbox), one named like a login a warning; a visitor sign-in without `instance`, and
  registration for site users, are errors. The agent's `describe_sign_in` says which plugins
  are installed, the instance slugs, sign-in methods, the main domain and this site's page rules.

## Page state, custom CSS, language

- **Page state** — `page.state` declares named values with defaults (`{ "tab": "dates",
  "open": false }`). Buttons `set-state` / `toggle-state` them; a node's `when`
  (`{ state, equals? }`) shows it only while the value matches — tabs, toggles, show/hide.
  The canvas always shows everything; preview and site hide. The shell has no state.
  Declared in Pages › page settings; *Show when* in the inspector.
- **Custom CSS** (Advanced, collapsed) — `node.css` holds declarations applied to that
  instance's element through a rule scoped to its id. Braces, `< >`, at-rules, escapes and
  `url()`-like functions are refused, by the schema and again at render.
- **Language** — `app.locale` sets `<html lang>` and how a bound ISO date reads in a text prop.

## The agent

The Agent tab runs the shared agent with kind `visual`. Its tools edit documents as trees:
`inspect_site`, `inspect_document`, `list_component_types`, `insert_node`, `move_node`,
`remove_node`, `duplicate_node`, `set_props` (with a device), `bind_props`, `set_action`,
`create_page`, `update_page`, `delete_page` (asks first), `create_component`,
`start_component_version`, `expose_setting`, `expose_slot`, `update_instances`,
`set_design_kit`, `set_css`, `set_when`, `inspect_selected` (what the author means by “this”), `list_connections`, `describe_apps` (each Dynamic Apps app's tables, fields, relationships and public access, as the site reads them), `describe_sign_in` (which of the two account plugins the site can use, and how it is set up), `check_visual_site`. `list_component_types` with `types` returns those components with every setting explained. The prompt's Data section separates plugin content, Dynamic Apps records and external APIs — three tools, three source shapes.

From the server's AI catalog the agent also gets an app's `dynamic-apps.config` reads, draft
preview and validation, and `apply_change_set` — only for apps whose AI tools are on and members
with the model permissions. A change set writes the app's draft and asks the author first even in
the default mode (the author's decision, 2026-10-19); publishing, discarding, rollback and flows
are not the site agent's: the author publishes on the app's Configuration screen. Every write re-validates the whole document and its
placement, and goes through the run transaction (reviewable, revertable). The run's gate is
`checkVisualSite` with the same content schema the Problems list uses.

## Developer components (P7)

A developer writes a React component in TSX; authors place and configure it like any other.

- **Contract** `dcms/code/<name>.json` — `{ schemaVersion, name, label, description?, category?,
  props }`, the same prop definitions built-ins use. The builder reads only this.
- **Source** `src/components/<name>.tsx` — its default export receives the props as React props.
  *My components › New developer component (TSX)* writes both; *Edit source* on a selected
  instance opens it in the code view, typed against React and the vendored runtime.
- **Type** `code.<name>`. `codeDefinitions(contracts, modules?, island?)` renders the developer's
  component when the modules are given — the published site's entry globs `src/components/*.tsx`
  — through the admin's `island` renderer on the canvas, and a placeholder otherwise.
- **On the canvas: islands.** The source never runs in the canvas (same origin as the admin).
  `canvas/islands.ts` bundles the site's components with the Mode B bundler into one island page
  from a virtual entry (`src/__dcms_island.tsx`, never saved or deployed), rebuilt as files
  change; each developer component is a frame of it with `sandbox="allow-scripts"` only — an
  opaque origin. `CodeIsland` sends it `{name, props}` as JSON and believes three answers, only
  from that frame: ready, a height (clamped to 4000px), an error. The frame takes no pointer
  events, so selection and drag stay on the node. The placeholder shows while it builds and on
  failure. Islands have no fetch proxy: a component's own requests work in the full preview and
  on the site.
- **Where else it runs.** Not in the in-admin preview, which shares the admin's origin: a site
  with any developer component previews in the Mode B sandbox instead — the whole
  draft bundled in the browser (esbuild-wasm, `import.meta.glob` expanded by
  `ide/preview/importGlob.ts`, packages pinned from the palette then the site's package.json),
  run in an opaque-origin `srcdoc` frame whose API calls the admin replays only inside the
  site's preview subtree.
- **Checks.** `checkVisualSite` wants a contract that parses, a source beside it with a default
  export, warns about a source with no contract, and about a contract setting the source never
  mentions (it cannot be reading it). Types and the build check the rest.
- **API explorer** — the rail's `{}` button opens the tenant API description (Scalar) the
  generated client in `src/api` is built from.
- **Dependencies.** `package.json` is in the code view. An agent edit to it is `dangerous` in
  every agent kind (`riskFor` on the file tools): the build installs what it declares.

## Publishing

Publish is gated on zero errors. It merges the draft into the release branch; the build is
`ReactAppBuilder`'s sandboxed Vite build, exactly as for Mode B.
Other branches can publish to their own domains: [publishing-branches.md](publishing-branches.md).

## Acceptance (P6)

The Events slice — kit, an Event card component bound to the Events plugin's `gig`
(stacks on mobile, links to its page), a hero, a collection, a shell footer and an
`/events/:slug` detail page — is built by agent tool calls in
`e2e/admin/visualAgent.spec.ts` and checked on the canvas and in preview. The same
documents built with the real `visual` template and Vite, served the site-host way, gave
the same result: every gig listed, the card a row on desktop and a column at 375px, card
links and direct deep links to the detail page, a 404 for an unknown slug, no console
errors.

A CRM slice over a Dynamic Apps app — a deals data table linking to `/deals/:id`, the deal's
detail page with an account lookup, the account's page listing its deals, and a record form — is
built by scripted agent calls in `e2e/admin/visualAgent.spec.ts`, after `describe_apps`.

Not yet verified by a person: the signed-in walk-through on vps1 (create → publish →
serve), and a prompt-driven run against a real model.

## Traps

- A slot's children are owned by GrapesJS; never let React render into a `.dcms-slot`.
- `node.id` is the identity everything addresses — selection, bindings, `scroll-to`,
  `open-modal`, the agent. Copies get fresh ids at birth.
- A binding with no item around it (outside a collection's item slot, on a non-detail
  page) is an error, not an empty string.
