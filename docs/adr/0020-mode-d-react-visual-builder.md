# ADR 0020: Mode D — a visual builder whose output is a real React application

**Status:** accepted (2026-10-02) · being implemented (kanban epic #55)

## Context

DCMS has three site modes. Mode A (ADR 0006) is a visual builder over HTML and CSS;
Mode B is a React application edited as source in the web IDE; Mode C is an uploaded
static bundle. None of them lets a non-technical author build an *application* — routes,
an app shell, reusable components, data from the tenant's plugins, forms and actions —
without writing code, and Mode A's model is HTML all the way down: block markup, a
placeholder contract read by `hydrate.js`, and a static page assembler.

Most of what such a builder needs exists already: GrapesJS and its React panels, the git
working-draft/branch/publish stack, Monaco and the VFS, the tenant OpenAPI → typed client
pipeline, the sandboxed Vite build, and the shared agent runtime. What is missing is a
React component model that the canvas, the published site and the AI all agree on.

## Decision

**Mode D (`SiteRenderMode.ReactBuilder`) is a new, additive mode.** Mode A stays, with no
migration between them and no retirement planned.

### A page is a tree of component instances, stored as JSON in the site's repo

```
dcms/app.json                     routes, navigation, app shell, SEO defaults
dcms/theme.json                   design tokens — the same shape Mode A's kits use
dcms/pages/<id>.json              { schemaVersion, id, title, seo, root: Node }
dcms/components/<name>/v<N>.json  tenant components, one immutable file per version
src/main.tsx                      generated: loads dcms/**.json, mounts the app
src/dcms/runtime/**               generated: the vendored @dcms/site-runtime
src/api/**, openapi.json          generated: the existing tenant API client
```

A node is `{ id, type, version?, props?, slots?: { name: Node[] }, responsive?, action? }`.
GrapesJS holds only the page being edited, maps to and from this tree, and never persists
anything of its own. GrapesJS's `project.json` is not used: its format would leak into the
runtime, the validator and the AI tools, every GrapesJS upgrade would be a persistence
migration, and its diffs are unreadable — the same reason ADR 0006 stores HTML rather than
a JSON blob.

### One package defines the components, and everything else reads it

`packages/site-runtime` (`@dcms/site-runtime`) holds the schemas, the component
definitions, the one placement rule (`canPlace`) and the renderer. The admin imports it
to draw the canvas; the published site runs a copy of the same source. Drag and drop, the
inspector, the AI tools, the validator and the publisher all ask the same `canPlace`, so
anything one of them refuses the others refuse too.

The package is **copied into each site** (`src/dcms/runtime/`), the way
`packages/api-client` already is (`src/api/runtime/`). It may therefore import only its own
files, `react`, `react-dom`, `react-router`, `zod` and `dompurify` — never another
`@dcms/*` package. Mode A's theme schema and theme CSS moved into it for that reason, and
`@dcms/gjs-schema` re-exports them.

### Publishing is the Mode B build

A Mode D repo is a small Vite application, built by the existing `ReactAppBuilder` in the
existing sandbox. There is no Mode D publisher. Each site carries the runtime version it was
built with, and the later developer mode (TSX components) needs no second pipeline.

### The product boundary is a controlled runtime

Basic Mode D offers no arbitrary JavaScript, hooks, event handlers or npm packages.
Behaviour is a declarative action (`navigate`, `open-external`, `scroll-to`, `submit-form`,
`open-modal`, `show-toast`) that the runtime executes. Developer-authored TSX components
are a separate, later layer.

### The canvas runs only platform code

The GrapesJS canvas iframe is **same-origin with the admin console**: code running in it
can read the operator's session. So only `@dcms/site-runtime`'s own components may execute
there. Tenant-built components are data interpreted by that code. Developer TSX never
executes in the canvas; it renders in an opaque-origin sandboxed frame, as the Mode B
preview does. Rich text is passed through DOMPurify before it reaches the DOM. Action
targets are validated (`open-external` accepts only `http(s):`, `mailto:` and `tel:`).

## Consequences

- One component set, one renderer: what the author sees on the canvas is the code that
  ships. There is no second "preview" implementation to keep in step, unlike
  `preview.ts` ↔ `hydrate.js` in Mode A.
- Every change to `@dcms/site-runtime` is a change to code that ships inside tenant sites.
  It is pinned per site at the version its generated layer was last refreshed to.
- The page format is a public contract. Each document carries `schemaVersion`; changing a
  shape means a migration, not an edit.
- Mode A and Mode D share the theme token vocabulary, so a design kit applies to both.
- The React-in-GrapesJS adapter (a React root per component, named slots as fixed child
  components) is the main technical risk and is proven by a go/no-go spike before the rest
  of the builder is built on it.
