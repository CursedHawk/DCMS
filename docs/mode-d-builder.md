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
DOMPurify. Developer-written TSX (P7) must never run there.

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
fallback, so deep links and link previews work without SSR.

## Components the tenant builds

A selection becomes a component (`dcms/components/<name>/v1.json`): a template tree plus
the settings (`props` → `bindings` onto inner nodes) and slots (`slotTargets` onto an
**empty** inner slot) it exposes. Instances pin a `version`. A version that pages use is
never edited in place: editing starts `v<N+1>`, and *Update to vN* migrates an instance,
dropping (and naming) settings the new version no longer has.

## Data

- **Collection** (`dcms.collection`, `props.source { instance, contentType }`) repeats its
  `item` slot per item; `empty` and `error` are slots too.
- **Detail page**: `page.data { source, param }` reads the item the route names; a missing
  item is a 404.
- **Bindings**: `node.bind` maps a prop to a field path — `title`, `values.colour`, or
  `#slug`/`#id`/`#index`/`#number`/`#count`. `BINDABLE` decides which field kinds a prop
  kind accepts. A list of media (an event's `photos`) binds to an image and shows its first.
- **Actions**: navigate (`/events/:slug` fills from the item), open-external, scroll-to,
  submit-form, open-modal, show-toast.
- **Forms**: `dcms.form` posts its fields to the Forms plugin.
- On the canvas, data comes from the **preview proxy** (`/api/admin/sites/{id}/preview/api/*`)
  — real content, sandboxed writes. Published sites read same-origin `/api/*`.

`checkVisualSite` checks sources and bound fields against the tenant's content types, so a
renamed field or a removed plugin shows up in Problems, not as an empty card.

## The agent

The Agent tab runs the shared agent with kind `visual`. Its tools edit documents as trees:
`inspect_site`, `inspect_document`, `list_component_types`, `insert_node`, `move_node`,
`remove_node`, `duplicate_node`, `set_props` (with a device), `bind_props`, `set_action`,
`create_page`, `update_page`, `delete_page` (asks first), `create_component`,
`start_component_version`, `expose_setting`, `expose_slot`, `update_instances`,
`set_design_kit`, `check_visual_site`. Every write re-validates the whole document and its
placement, and goes through the run transaction (reviewable, revertable). The run's gate is
`checkVisualSite` with the same content schema the Problems list uses.

## Publishing

Publish is gated on zero errors. It merges the draft into the release branch; the build is
`ReactAppBuilder`'s sandboxed Vite build, exactly as for Mode B.

## Acceptance (P6)

The Events slice — kit, an Event card component bound to the Events plugin's `gig`
(stacks on mobile, links to its page), a hero, a collection, a shell footer and an
`/events/:slug` detail page — is built by agent tool calls in
`e2e/admin/visualAgent.spec.ts` and checked on the canvas and in preview. The same
documents built with the real `visual` template and Vite, served the site-host way, gave
the same result: every gig listed, the card a row on desktop and a column at 375px, card
links and direct deep links to the detail page, a 404 for an unknown slug, no console
errors.

Not yet verified by a person: the signed-in walk-through on vps1 (create → publish →
serve), and a prompt-driven run against a real model.

## Traps

- A slot's children are owned by GrapesJS; never let React render into a `.dcms-slot`.
- `node.id` is the identity everything addresses — selection, bindings, `scroll-to`,
  `open-modal`, the agent. Copies get fresh ids at birth.
- A binding with no item around it (outside a collection's item slot, on a non-detail
  page) is an error, not an empty string.
- Dates bound to text props print as stored (ISO); there is no date formatting yet.
