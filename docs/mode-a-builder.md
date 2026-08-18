# The Mode A visual builder

Mode A sites (`SiteRenderMode.StaticPrerender`) are authored in a drag-and-drop
builder over **real HTML and CSS files in a git repository**. See
[ADR 0006](adr/0006-mode-a-html-css-in-git.md) for why.

## What a site is

```
site.json               version 2 — theme tokens, nav, page index, SEO, settings
pages/<slug>.html       the page body, a fragment (no <html>/<head>/<body>)
styles/global.css       rules shared by every page
styles/pages/<slug>.css rules for one page
styles/theme.css        GENERATED from site.json.theme — read-only in the editor
assets.json             asset manager entries, referencing the DCMS media library
```

Anything else in the repo (`robots.txt`, committed images) is copied to the
published site untouched.

Cascade order on a published page is theme → global → page, and the home page is
published both at its path and at `index.html`.

## Editing surfaces

The toolbar switches between **Design**, **Split** and **Code**. All three edit
the same working draft:

- **Design** — the GrapesJS canvas. Blocks, layers, styles, traits and pages are
  rendered by DCMS's own React panels (every GrapesJS manager runs in
  `custom: true` mode), so the chrome is the admin design system and the asset
  picker is the real media library.
- **Code** — Monaco over `pages/*.html`, `styles/*.css` and `site.json`.
- **Split** — both, live. The canvas re-reads a page whose file changed
  underneath it, and a canvas edit shows up in the code immediately.

The canvas holds **one page at a time**. GrapesJS's CSS store is global, so
holding every page's rules at once would let one page style another and diverge
from the published cascade. Every rule is tagged with the file it came from and
written back to that file.

## Components

A component is identified by a **CSS class**, not a marker attribute:
`<section class="dcms-hero">` is a Hero. This means markup typed by hand in the
code view comes back as a real, editable component on the canvas — and that the
committed HTML carries no editor bookkeeping.

The catalogue is defined once, as `DcmsComponentSpec` values in
`packages/gjs-schema` and `packages/gjs-blocks`, and feeds three things that must
never disagree: the palette, the canvas component types, and the code view's
completions and diagnostics.

### Plugin components

Every **enabled** plugin instance generates components from its manifest's content
types: a list per type, and a detail view where the type has a slug field. Two
instances of one plugin are distinct components. Installing a plugin adds blocks
to the palette on the next load — no deploy, no admin code.

They publish as the placeholder contract the runtime already reads:

```html
<div class="dcms-plugin-blog-post-list"
     data-dcms-component="plugin:blog:post:list"
     data-dcms-props='{"heading":"Latest posts","layout":"cards"}'
     data-dcms-bindings='[{"propPath":"items","instanceSlug":"blog",
                           "query":{"contentType":"post","pageSize":6}}]'></div>
```

On the canvas these show **live tenant content**, fetched from the admin content
API and painted into the element (never into the model, so no preview is ever
committed). On the published page `_dcms/hydrate.js` fetches from the delivery
API and draws the same layouts.

> `packages/gjs-blocks/src/preview.ts` (canvas) and
> `src/Services/Dcms.SiteBuilder/Runtime/hydrate.js` (published) are two
> implementations of one set of layouts — the runtime must stay dependency-free
> ES5 for every published site. **Change them together.** `preview.test.ts` pins
> the class names both produce.

Presentation is chosen in the inspector and travels in `data-dcms-props`:
`layout` (cards / list / article / video / audio / downloads), `heading`,
`emptyText`, and `titleField` / `bodyField` / `imageField` / `linkField`, which
map content fields to card slots instead of guessing from key names.

What is fetched travels in the binding query: `pageSize` and `page` for a list,
`itemSlug` for a detail view (empty means "whatever the page URL names"). There
is deliberately no sort or filter control — the delivery API accepts neither, and
a setting the server cannot honour is worse than no setting.

## Version control

Identical to Mode B, because it is the same code
(`apps/admin/src/features/site-source/`):

- a per-site Forgejo repo, provisioned on first open;
- a per-user, per-branch working draft, autosaved as granular deltas with
  per-file conflict detection (two tabs on different files merge; on the same
  file the second gets a 409 instead of clobbering the first);
- branches, diffs with `+N −M` counts, merge with conflict resolution, restore
  from any commit;
- **Publish** = ship this branch to `release`, whose push webhook builds and
  deploys.

An AI generation is an ordinary uncommitted change: it lands in the working draft
and is reviewed, or discarded, in the Source Control panel like any other edit.

## Language intelligence in the code view

Three layers, none on the main thread:

1. **Monaco's html worker**, taught the component catalogue as `HTMLDataV1` — tag,
   attribute and attribute-value completion plus hover docs for every block and
   every plugin component.
2. **Monaco's json worker**, given a JSON Schema for `site.json`.
3. **The parse worker**, for everything that needs the whole project: class and
   `var(--dcms-*)` completion from the project's own CSS, go-to-definition from a
   class to its rule and from `var()` to its declaration, find-all-references from
   a CSS selector to every page using it, and cross-file diagnostics —
   unknown component, unknown plugin instance, a content type that instance does
   not offer, malformed props/bindings JSON, duplicate id, undefined class,
   missing alt text, `target="_blank"` without `rel="noopener"`.

Diagnostics are published for **every** page, not only the open one.

## Layout model

Flow-first: sections, flex and grid. Absolute positioning is opt-in per container
via the **Free canvas** block, which flips `dragMode: 'absolute'` for its subtree
only — preserving the old free-canvas capability without making it the default.

## Extending the catalogue

Add a `DcmsComponentSpec` to `packages/gjs-blocks/src/specs/`. Its round-trip test
runs automatically: the block is inserted, serialized, re-parsed and serialized
again, and the two must be byte-identical. A block that fails that corrupts the
author's page on the next autosave, so the test is not optional.

Default styling for a new block belongs in `blocks-css.ts` and must be written in
`var(--dcms-*)` terms so it follows the site's theme.
