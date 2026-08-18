# ADR 0006: Mode A sites are HTML/CSS files in git, edited with GrapesJS

**Status:** accepted (2026-08-18)

## Context

Mode A (`SiteRenderMode.StaticPrerender`) was a bespoke free-canvas editor: a flat
list of absolutely-positioned `ComponentNode`s in a zustand store, typed by
`@dcms/editor-core`'s `SiteDefinition` schema, stored as one JSON blob per site,
and rendered by a hand-written C# tree-walker (`SiteRenderer`). It had ~13
component types, no version control, no code view, and every new capability cost
bespoke editor code on both sides of the wire.

Mode B (`ReactApp`) had meanwhile grown the infrastructure the platform actually
needed: a per-site Forgejo repo, per-user/per-branch working drafts, granular
autosave with per-file conflict detection, branches, diffs, merge-with-resolution,
restore, and a `release` branch whose pushes trigger build and deploy.

The question was not "which editor" but **what a Mode A site is**. A JSON document
that only our editor can read cannot be diffed, reviewed, branched, merged, or
hand-edited; a directory of HTML and CSS can.

## Decision

**A Mode A site is a git repository of HTML and CSS files**, in the same
`SiteFileMap` shape Mode B already uses:

```
site.json              version 2: theme tokens, nav, page index, SEO, settings
pages/<slug>.html      the page body — a fragment, not a document
styles/global.css      shared rules
styles/pages/<slug>.css page-scoped rules
styles/theme.css       GENERATED from site.json.theme; never hand-edited
```

**The editor is GrapesJS core** (BSD-3-Clause, no licence key; the commercial
Studio SDK is deliberately not used), wrapped in DCMS's own React panels — the
Block, Style, Trait, Layer and Asset managers all run in `custom: true` mode, so
the chrome is the admin design system and the Asset Manager is the real DCMS media
library.

Four decisions follow from the format:

1. **Component identity is a CSS class**, not a marker attribute. A `Hero` is
   `<section class="dcms-hero">`. The class is already meaningful — it is what the
   CSS targets — it keeps committed HTML free of editor bookkeeping, and it means
   markup typed by hand in the code view comes back as a real, editable component
   on the canvas. Every registered type declares both `isComponent` (DOM path) and
   `isParsedNode` (worker path) so this survives either parser.

2. **Data-bound components keep the placeholder contract already in production.**
   `data-dcms-component` / `-props` / `-bindings`, read by
   `src/Services/Dcms.SiteBuilder/Runtime/hydrate.js`. What the canvas holds is
   byte-for-byte what is committed and what hydrates — there is no editor-only
   representation to translate at publish time.

3. **Version control is Mode B's, unchanged.** The shared modules were lifted into
   `apps/admin/src/features/site-source/`; the builder gets branches, diffs,
   merges, restore and build-on-push for free, and "publish" means the same thing
   in both modes.

4. **The string-heavy work runs in web workers.** GrapesJS's `parsersCode` /
   `parserCss` hooks accept plain data structures, so HTML (htmlparser2), CSS
   (css-tree), formatting (js-beautify), the project symbol index, cross-file
   diagnostics and Source Control line diffs all happen off the main thread.
   GrapesJS itself is Backbone + a canvas iframe and stays on it.

**No migration.** Existing Mode A definitions are not converted; the old editor,
`packages/editor-core` and the v1 component registry are deleted. The old format
had no released tenants worth a converter, and a half-faithful conversion would
have been worse than a clean start.

## Consequences

- A Mode A site is reviewable. `pages/home.html` in a diff is readable by anyone,
  including someone who has never opened the builder.
- The code view is not a preview: Monaco edits the same working draft the canvas
  writes, so the two are one document rather than two representations that can
  disagree. Diagnostics that need the whole project — an unknown component, a
  binding naming a plugin instance this tenant does not have, a class nothing
  defines — run in a worker and appear as Monaco markers.
- Tenant plugins generate their own components at load time from the plugin
  manifests and instances, so installing a plugin adds blocks to the palette with
  no deploy and no admin code.
- `hydrate.js` gained a presentation contract (`layout`, field mapping,
  `emptyText`, detail-fetch by slug) and is now mirrored by
  `packages/gjs-blocks/src/preview.ts`, which draws the same layouts in the canvas.
  These are two implementations by necessity — the runtime ships as
  dependency-free ES5 to every published site — and must be changed together.
- **A Mode A repo can hold arbitrary committed HTML.** That is the same trust
  level Mode B already grants (tenant admins ship arbitrary React), so no
  server-side sanitizer was added. The *editor's* parser stays at
  `allowScripts: false` / `allowUnsafeAttr: false`; the Custom Code block is the
  explicit, opt-in escape hatch.
- The round-trip test (`packages/gjs-blocks`) is load-bearing: every block is
  inserted, serialized, re-parsed and serialized again, and the two must be
  byte-identical. A block that fails it corrupts the author's page on the next
  autosave.
