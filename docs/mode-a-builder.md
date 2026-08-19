# The Mode A visual builder

Mode A sites (`SiteRenderMode.StaticPrerender`) are authored in a drag-and-drop
builder over **real HTML and CSS files in a git repository**. See
[ADR 0006](adr/0006-mode-a-html-css-in-git.md) for why.

## What a site is

```
site.json               version 2 — theme tokens, nav, page index, regions,
                        layouts, SEO, settings
pages/<slug>.html       the page body, a fragment (no <html>/<head>/<body>)
regions/<slug>.html     a shared band — header, footer — wrapped around pages
blocks/<name>.json      a component the tenant built themselves
styles/global.css       rules shared by every page
styles/pages/<slug>.css rules for one page
styles/theme.css        GENERATED from site.json.theme — read-only in the editor
assets.json             asset manager entries, referencing the DCMS media library
```

Anything else in the repo (`robots.txt`, committed images) is copied to the
published site untouched. `pages/`, `regions/` and `blocks/` are **not**:
they are assembled into documents, and publishing them as files would expose the
source and serve markup fragments at guessable URLs.

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

The canvas holds **one document at a time** — a page, a shared region or a
component template. GrapesJS's CSS store is global, so holding every page's rules
at once would let one page style another and diverge from the published cascade.
Every rule is tagged with the file it came from and written back to that file; a
region and a component have no stylesheet of their own, so rules authored while
editing one land in `global.css`, which is where a band shown on every page
belongs anyway.

## Shared layout: regions and layouts

Navigation, headers, footers and breadcrumbs are not part of any page. A
**region** is `regions/<slug>.html` plus an entry in `site.json`; a **layout**
names regions in order and says which pages get them:

```jsonc
"regions": [
  { "id": "r-head", "slug": "header", "label": "Header", "placement": "before" },
  { "id": "r-foot", "slug": "footer", "label": "Footer", "placement": "after" }
],
"layouts": [
  { "id": "default", "label": "Default", "regions": ["r-head", "r-foot"], "default": true }
]
```

A page's `layout` has three states, and the difference matters: **absent** means
the site default, an **id** picks a layout, and **`""`** means no shared chrome at
all — which is what a landing page that must not show the site navigation needs.

On the canvas the regions are painted around the page as **plain DOM outside the
GrapesJS wrapper**, never as components: everything inside the wrapper is
serialized into `pages/*.html`, so a region drawn as components would be copied
into every page that shows it and then drift from the region file. Clicking one
opens it on the canvas as its own document.

Two things inside a region cannot be authored as fixed markup, because the same
markup is shown on every page — so they carry `data-dcms-nav` and are resolved
per page:

| Attribute | Effect |
| --- | --- |
| `data-dcms-nav="breadcrumbs"` | The trail is rebuilt from the page URL, naming each level from the site's own page titles and humanising a segment that has no page of its own |
| `data-dcms-nav="menu"` | The link matching the current page gets `aria-current="page"`; a stale mark is cleared |

The published page carries a `dcms-routes` JSON block (every page's path and
title) so `hydrate.js` can do this without an API. The canvas resolves the same
thing against the page being edited, so a breadcrumb bar looks right while it is
being built.

## Components

A component is identified by a **CSS class**, not a marker attribute:
`<section class="dcms-hero">` is a Hero. This means markup typed by hand in the
code view comes back as a real, editable component on the canvas — and that the
committed HTML carries no editor bookkeeping.

The catalogue is defined once, as `DcmsComponentSpec` values in
`packages/gjs-schema` and `packages/gjs-blocks`, and feeds three things that must
never disagree: the palette, the canvas component types, and the code view's
completions and diagnostics.

Components come in two grains, and both are real:

- **Sections** (`specs/sections.ts`) — a whole band of a page. Each carries
  `data-tone`, `data-padding` and `data-width`, so alternating a page's bands is
  a dropdown rather than CSS. On a coloured band the headings, links and muted
  text inherit, so nothing needs correcting afterwards.
- **Parts** (`specs/parts.ts`) — the repeatable pieces a section is built from:
  Card, Feature, Pricing plan, Person, Statistic, Step, Timeline entry, Question,
  Testimonial, Slide, Section intro, Button row. They are their own components
  because the operation an author reaches for most is "add another one of these",
  and that only works if the piece has its own palette tile and its own traits
  rather than being anonymous markup inside a section's snippet.

### Design kits

`packages/gjs-blocks/src/kits.ts` holds six complete looks — studio, editorial,
bold, soft, noir, terra. A kit is **only theme tokens**: a palette, a type scale,
a shadow ramp, spacing and metrics. Applying one rewrites the generated
`styles/theme.css` and touches nothing the author wrote, which is what makes
trying six looks non-destructive and reversible.

No kit loads a web font. A kit that reached for a font CDN would put every
visitor's first paint behind a third party, on a page that otherwise fetches
nothing.

The contract between kits and the block stylesheet is enforced by `kits.test.ts`,
in both directions: `blocks-css.ts` may contain no colour literal (a literal is a
value no kit can reach), and every kit must define every token the stylesheet
reads (an undefined token does not error — the declaration is silently dropped
and that block renders with no radius, or no colour at all).

`styles/global.css` is seeded from `blocks-css.ts` when a site is created and is
the author's from then on, so library improvements do not reach an existing site.
The design-kit picker offers an explicit **Refresh block styles** for authors who
want the newer sheet; it overwrites that one file and says so before it does.

### Palette thumbnails

Blocks are shown as wireframes, drawn per archetype in
`packages/gjs-blocks/src/thumbnails.ts` and picked from the spec — its type, then
its icon, then its category, and for a generated plugin block from the layout its
content type implies. A 22px glyph says a block exists; the wireframe says what
dropping it will produce, which is the only question the palette is being asked.
Structure is `currentColor`; anything the eye should land on first carries
`.dcms-thumb-accent` for the panel to paint, so the palette follows the admin
theme in light and dark.

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
> ES5 for every published site. **Change them together.** `runtimeParity.test.ts`
> reads the runtime as text and fails if the two disagree about the layout
> vocabulary, the prop names or the class names.

Everything a plugin block renders is styled by the site's own `styles/global.css`
from theme tokens, so plugin content follows the design kit like a hand-built
section does and stays editable. `hydrate.js` injects only a `:where()` fallback
at zero specificity, for pages whose stylesheet predates those rules — the site's
CSS always wins, whatever order the sheets load in.

Presentation is chosen in the inspector and travels in `data-dcms-props`:

| Prop | What it does |
| --- | --- |
| `layout` | What one item looks like: cards, tiles, list, compact, feature, article, video, audio, downloads |
| `arrangement` | How items are placed: grid, masonry, carousel (cards and tiles) |
| `columns` | 1–4, or absent to fit as many as fit. Collapses to one on a phone regardless |
| `cardVariant` | outline / raised / soft / plain |
| `heading`, `subheading`, `moreLabel`, `moreHref` | The heading row, with a "see all" link beside the title rather than orphaned under the last card |
| `titleField`, `bodyField`, `imageField`, `metaField`, `tagsField`, `linkField` | Which field fills each slot. Three states: empty guesses from the key name, a field name overrides the guess, `-` leaves the slot empty |
| `excerptLength` | Trim body text to N characters, cut at a word |
| `emptyText`, `linkLabel` | The empty state, and the per-item link text |

A date in the meta slot is formatted for the reader; anything else is passed
through, because that slot is as often a category as a timestamp.

A detail view is offered a subset: the controls that only describe a collection
(`arrangement`, `columns`, `cardVariant`, the "see all" link) are omitted rather
than shown and ignored.

What is fetched travels in the binding query: `pageSize` and `page` for a list,
`itemSlug` for a detail view (empty means "whatever the page URL names"). There
is deliberately no sort or filter control — the delivery API accepts neither, and
a setting the server cannot honour is worse than no setting.

## Components the tenant builds

The generated blocks can only ever guess at what a tenant's content looks like:
the same plugin serves `title`/`perex`/`obrazek` for one tenant and
`headline`/`standfirst`/`hero` for the next, and a fixed set of card layouts
with fixed slots runs out exactly when a site starts being specific. A tenant
component is the escape hatch that is not "write a plugin".

A definition is `blocks/<name>.json` — versioned, diffed and published with the
site:

```jsonc
{
  "version": 1,
  "name": "post-card",
  "label": "Post card",
  "source": { "instanceSlug": "blog", "contentType": "post", "mode": "list" },
  "props": [{ "name": "prop1", "label": "Heading", "kind": "text" }],
  "template": "<div class=\"dcms-c-post-card\">…</div>"
}
```

The template is **ordinary HTML carrying binding attributes**, not a string with
`{{ }}` holes in it. That is what lets the component builder be the same canvas:
the author drags real blocks, and a binding is an attribute on the element they
selected, set in the inspector's **Data** tab.

| Attribute | Meaning |
| --- | --- |
| `data-dcms-bind="text:title"` | What fills this element, and where it lands (`text`, `html`, `src`, `href`, `alt`, `title`, `style:background-image`, `class`) |
| `data-dcms-repeat` | Drawn once per fetched item; everything inside reads that item |
| `data-dcms-if` / `data-dcms-unless` | Drop the element when the named source is empty / has a value |
| `data-dcms-format` | `date`, `media`, or `raw` to opt out |
| `data-dcms-truncate`, `data-dcms-prefix`, `data-dcms-suffix`, `data-dcms-fallback` | Trim, wrap, and what to show when the source resolves to nothing |
| `data-dcms-empty` | Kept only when there are no items at all |

A source is `@name` for a component prop, `#slug`/`#id`/`#index` for the item
envelope, and anything else is a field path. **Tenant-defined fields are offered
at the path their values actually live at** (`values.perex`, not `perex`) —
binding to the bare key renders nothing, which looks exactly like an empty field.

The two kinds are derived, not declared: a definition **with** a source publishes
as the same inert placeholder every plugin component uses and is filled in by
`hydrate.js`; one **without** expands into the page as real markup the moment it
is dropped, so a static page keeps its content with no JavaScript at all.

Every definition becomes an ordinary `DcmsComponentSpec`, so a tenant component
gets the palette tile, the trait panel, the code-view completions and the
diagnostics for free, and cannot drift from them. Its props become traits. Each
published page embeds the definitions it might need as a `dcms-components` JSON
block, so a component renders without a second request.

Nothing is substituted into the template on the canvas — the elements there are
the author's to edit, and typing over a preview would bake one item's text into
the template for every item. Instead empty bound elements say what they are bound
to, and a repeat is outlined.

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

## Positioning model

Flow-first: sections, flex and grid. Absolute positioning is opt-in per container
via the **Free canvas** block, which flips `dragMode: 'absolute'` for its subtree
only — preserving the old free-canvas capability without making it the default.

## Extending the catalogue

Add a `DcmsComponentSpec` to `packages/gjs-blocks/src/specs/`. Its round-trip test
runs automatically: the block is inserted, serialized, re-parsed and serialized
again, and the two must be byte-identical. A block that fails that corrupts the
author's page on the next autosave, so the test is not optional.

Default styling for a new block belongs in `blocks-css.ts` and must be written in
`var(--dcms-*)` terms so it follows the site's theme. If it reads a token no kit
defines, add it to every kit — `kits.test.ts` will tell you which.

Give the block an archetype in `thumbnails.ts` too. Without one it falls back to
its category's drawing, which is right often enough to be easy to miss and wrong
often enough to matter.
