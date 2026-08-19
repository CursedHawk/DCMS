/**
 * Default styling for the block catalogue.
 *
 * This is seeded into a new site's `styles/global.css` rather than shipped as a
 * runtime stylesheet, because the file belongs to the author: they can edit any
 * of it in the code view, and a published page links one plain stylesheet with
 * no build step. The trade-off is that library changes do not reach existing
 * sites — which is the right way round for a website builder, where a silent
 * restyle of a live site would be far worse than a stale default. (The builder's
 * design-kit picker offers an explicit "refresh block styles" for authors who do
 * want the newer sheet.)
 *
 * **Every value here is a theme token.** Not one hard-coded colour, size, radius
 * or shadow — that is the whole mechanism behind design kits: swapping the token
 * bundle in `site.json` restyles the entire site, so the author gets a considered
 * look without writing CSS. A literal added to this file is a value no kit can
 * reach, so `kits.test.ts` fails the build if one appears — in both directions:
 * it also fails if a kit leaves a token this sheet reads undefined, which does
 * not error at runtime, it just silently drops the declaration.
 *
 * The one exception is structural CSS — `display`, `position`, `grid-template`,
 * the breakpoint widths — which describes how a block is built rather than how
 * it looks, and is not something a kit should be able to change.
 */
export const BLOCKS_CSS = `/* ---------------------------------------------------------------
   DCMS block library — default styles. Yours to edit.
   Every value comes from a theme token in styles/theme.css, so
   changing the design kit restyles all of this at once.
   --------------------------------------------------------------- */

*,
*::before,
*::after { box-sizing: border-box; }

html { -webkit-text-size-adjust: 100%; }

body {
  margin: 0;
  font-family: var(--dcms-font-body);
  font-size: var(--dcms-text-base);
  line-height: var(--dcms-leading-body);
  color: var(--dcms-color-text);
  background: var(--dcms-color-surface);
  -webkit-font-smoothing: antialiased;
}

/* --- Typography base ------------------------------------------- */

h1, h2, h3, h4, h5, h6 {
  font-family: var(--dcms-font-heading);
  font-weight: var(--dcms-weight-heading);
  line-height: var(--dcms-leading-heading);
  letter-spacing: var(--dcms-tracking-heading);
  text-transform: var(--dcms-transform-heading);
  color: var(--dcms-color-heading);
  margin: 0 0 var(--dcms-space-sm);
  text-wrap: balance;
}

h1 { font-size: var(--dcms-text-4xl); }
h2 { font-size: var(--dcms-text-3xl); }
h3 { font-size: var(--dcms-text-xl); }
h4 { font-size: var(--dcms-text-lg); }
h5 { font-size: var(--dcms-text-base); }
h6 { font-size: var(--dcms-text-sm); letter-spacing: var(--dcms-tracking-label); text-transform: uppercase; }

p { margin: 0 0 var(--dcms-space-md); text-wrap: pretty; }
p:last-child { margin-bottom: 0; }

small { font-size: var(--dcms-text-sm); }
code, kbd, pre, samp { font-family: var(--dcms-font-mono); font-size: 0.9em; }

img, svg, video, canvas { max-width: 100%; height: auto; display: block; }

a {
  color: var(--dcms-color-brand);
  text-underline-offset: 0.2em;
  transition: color var(--dcms-transition);
}
a:hover { color: var(--dcms-color-brand-strong); }

:focus-visible {
  outline: 2px solid var(--dcms-color-brand);
  outline-offset: 2px;
  border-radius: var(--dcms-radius-sm);
}

::selection { background: var(--dcms-color-brand-soft); color: var(--dcms-color-heading); }

hr { border: 0; border-top: var(--dcms-border-width) solid var(--dcms-color-border); margin: var(--dcms-space-lg) 0; }

/* --- Layout ---------------------------------------------------- */

.dcms-section {
  padding-block: var(--dcms-section-py);
  padding-inline: var(--dcms-space-lg);
  position: relative;
}
.dcms-section[data-padding="none"] { padding-block: 0; }
.dcms-section[data-padding="sm"] { padding-block: var(--dcms-space-lg); }
.dcms-section[data-padding="lg"] { padding-block: calc(var(--dcms-section-py) * 1.6); }
.dcms-section[data-width="full"] { padding-inline: 0; }

/* Section tones. A page reads as designed when its bands alternate, and this
   is the control that does it — one attribute, no CSS to write. */
.dcms-section[data-tone="alt"] { background: var(--dcms-color-surface-alt); }
.dcms-section[data-tone="sunken"] { background: var(--dcms-color-surface-sunken); }
.dcms-section[data-tone="brand"] {
  background: var(--dcms-color-brand);
  color: var(--dcms-color-brand-contrast);
}
.dcms-section[data-tone="brand-soft"] { background: var(--dcms-color-brand-soft); }
.dcms-section[data-tone="inverse"] {
  background: var(--dcms-color-inverse);
  color: var(--dcms-color-inverse-text);
}
.dcms-section[data-tone="gradient"] {
  background: linear-gradient(135deg, var(--dcms-color-brand), var(--dcms-color-accent));
  color: var(--dcms-color-brand-contrast);
}

/* On a coloured band everything inherits, so headings and links stop fighting
   the background instead of each needing an override. */
.dcms-section[data-tone="brand"] :is(h1, h2, h3, h4, h5, h6, a),
.dcms-section[data-tone="inverse"] :is(h1, h2, h3, h4, h5, h6, a),
.dcms-section[data-tone="gradient"] :is(h1, h2, h3, h4, h5, h6, a) { color: inherit; }
.dcms-section[data-tone="brand"] .dcms-muted,
.dcms-section[data-tone="inverse"] .dcms-muted,
.dcms-section[data-tone="gradient"] .dcms-muted { color: inherit; opacity: 0.8; }

.dcms-container { max-width: var(--dcms-container); margin-inline: auto; }
.dcms-section[data-width="narrow"] .dcms-container { max-width: var(--dcms-container-narrow); }
.dcms-section[data-width="full"] .dcms-container { max-width: none; }

.dcms-row { display: flex; flex-wrap: wrap; gap: var(--dcms-space-md); }
.dcms-row[data-gap="none"] { gap: 0; }
.dcms-row[data-gap="sm"] { gap: var(--dcms-space-sm); }
.dcms-row[data-gap="lg"] { gap: var(--dcms-space-lg); }
.dcms-row[data-gap="xl"] { gap: var(--dcms-space-xl); }
.dcms-row[data-align="start"] { align-items: flex-start; }
.dcms-row[data-align="center"] { align-items: center; }
.dcms-row[data-align="end"] { align-items: flex-end; }
.dcms-row[data-justify="center"] { justify-content: center; }
.dcms-row[data-justify="between"] { justify-content: space-between; }
.dcms-row[data-reverse="true"] { flex-direction: row-reverse; }

.dcms-col { flex: 1 1 16rem; min-width: 0; }
.dcms-col[data-span="1-4"] { flex: 0 0 calc(25% - var(--dcms-space-md)); }
.dcms-col[data-span="1-3"] { flex: 0 0 calc(33.333% - var(--dcms-space-md)); }
.dcms-col[data-span="1-2"] { flex: 0 0 calc(50% - var(--dcms-space-md)); }
.dcms-col[data-span="2-3"] { flex: 0 0 calc(66.666% - var(--dcms-space-md)); }
.dcms-col[data-span="3-4"] { flex: 0 0 calc(75% - var(--dcms-space-md)); }

.dcms-grid {
  display: grid;
  gap: var(--dcms-space-md);
  grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr));
}
.dcms-grid[data-gap="sm"] { gap: var(--dcms-space-sm); }
.dcms-grid[data-gap="lg"] { gap: var(--dcms-space-lg); }
.dcms-grid[data-gap="xl"] { gap: var(--dcms-space-xl); }
.dcms-grid[data-columns="2"] { grid-template-columns: repeat(2, minmax(0, 1fr)); }
.dcms-grid[data-columns="3"] { grid-template-columns: repeat(3, minmax(0, 1fr)); }
.dcms-grid[data-columns="4"] { grid-template-columns: repeat(4, minmax(0, 1fr)); }
.dcms-grid[data-columns="5"] { grid-template-columns: repeat(5, minmax(0, 1fr)); }
.dcms-grid[data-columns="6"] { grid-template-columns: repeat(6, minmax(0, 1fr)); }

/* A "bento" grid: the first cell spans two, the rest fill in. Gives a landing
   page some hierarchy without the author positioning anything. */
.dcms-grid[data-feature="first"] > :first-child { grid-column: span 2; grid-row: span 2; }

.dcms-stack { display: flex; flex-direction: column; gap: var(--dcms-space-md); }
.dcms-stack[data-direction="row"] { flex-direction: row; flex-wrap: wrap; }
.dcms-stack[data-gap="sm"] { gap: var(--dcms-space-sm); }
.dcms-stack[data-gap="lg"] { gap: var(--dcms-space-lg); }
.dcms-stack[data-align="center"] { align-items: center; }

.dcms-spacer { height: var(--dcms-space-md); }
.dcms-spacer[data-size="sm"] { height: var(--dcms-space-sm); }
.dcms-spacer[data-size="lg"] { height: var(--dcms-space-lg); }
.dcms-spacer[data-size="xl"] { height: var(--dcms-space-xl); }

.dcms-divider { border: 0; border-top: var(--dcms-border-width) solid var(--dcms-color-border); margin: var(--dcms-space-lg) 0; }
.dcms-divider[data-style="dashed"] { border-top-style: dashed; }
.dcms-divider[data-style="thick"] { border-top-width: calc(var(--dcms-border-width) * 3); }

/* --- Typography components ------------------------------------- */

/* Section intros. The pattern is the same everywhere — eyebrow, heading, one
   supporting line — so it is one class rather than a decision per section. */
.dcms-section-head { margin-bottom: var(--dcms-space-lg); }
.dcms-section-head[data-align="center"] { text-align: center; }
.dcms-section-head[data-align="center"] .dcms-section-lead { margin-inline: auto; }
.dcms-eyebrow {
  display: block;
  margin: 0 0 var(--dcms-space-2xs);
  font-size: var(--dcms-text-sm);
  font-weight: 600;
  letter-spacing: var(--dcms-tracking-label);
  text-transform: uppercase;
  color: var(--dcms-color-brand);
}
.dcms-section-lead {
  max-width: var(--dcms-container-narrow);
  font-size: var(--dcms-text-lg);
  color: var(--dcms-color-muted);
  margin-bottom: 0;
}

.dcms-muted { color: var(--dcms-color-muted); }
.dcms-rich-text > :last-child { margin-bottom: 0; }
.dcms-rich-text :is(h2, h3, h4) { margin-top: var(--dcms-space-lg); }

.dcms-blockquote {
  margin: 0;
  padding-left: var(--dcms-space-md);
  border-left: calc(var(--dcms-border-width) * 3) solid var(--dcms-color-brand);
  font-size: var(--dcms-text-lg);
}
.dcms-blockquote cite {
  display: block;
  margin-top: var(--dcms-space-sm);
  color: var(--dcms-color-muted);
  font-style: normal;
  font-size: var(--dcms-text-sm);
}
.dcms-blockquote[data-variant="card"] {
  padding: var(--dcms-space-lg);
  border-left: 0;
  border-radius: var(--dcms-radius-lg);
  background: var(--dcms-color-surface-alt);
  box-shadow: var(--dcms-shadow-sm);
}
.dcms-blockquote[data-variant="large"] { font-size: var(--dcms-text-2xl); border-left: 0; padding-left: 0; }

.dcms-list { padding-left: 1.25rem; }
.dcms-list[data-style="decimal"] { list-style: decimal; }
.dcms-list[data-style="none"] { list-style: none; padding-left: 0; }
.dcms-list[data-style="check"] { list-style: none; padding-left: 0; }
.dcms-list[data-style="check"] li { padding-left: 1.6em; position: relative; }
.dcms-list[data-style="check"] li::before {
  content: '';
  position: absolute;
  left: 0;
  top: 0.45em;
  width: 0.9em;
  height: 0.5em;
  border-left: 2px solid var(--dcms-color-brand);
  border-bottom: 2px solid var(--dcms-color-brand);
  transform: rotate(-45deg);
}
.dcms-list li + li { margin-top: var(--dcms-space-2xs); }

.dcms-code-block {
  overflow-x: auto;
  padding: var(--dcms-space-md);
  border-radius: var(--dcms-radius);
  background: var(--dcms-color-surface-sunken);
  border: var(--dcms-border-width) solid var(--dcms-color-border);
  font-size: var(--dcms-text-sm);
}

.dcms-badge {
  display: inline-block;
  padding: 0.2em 0.7em;
  border-radius: var(--dcms-radius-pill);
  font-size: var(--dcms-text-xs);
  font-weight: 600;
  letter-spacing: var(--dcms-tracking-label);
  background: var(--dcms-color-brand);
  color: var(--dcms-color-brand-contrast);
}
.dcms-badge[data-tone="neutral"] { background: var(--dcms-color-surface-sunken); color: var(--dcms-color-text); }
.dcms-badge[data-tone="soft"] { background: var(--dcms-color-brand-soft); color: var(--dcms-color-brand-strong); }
.dcms-badge[data-tone="success"] { background: var(--dcms-color-success); color: var(--dcms-color-brand-contrast); }
.dcms-badge[data-tone="warning"] { background: var(--dcms-color-warning); color: var(--dcms-color-brand-contrast); }
.dcms-badge[data-tone="danger"] { background: var(--dcms-color-danger); color: var(--dcms-color-brand-contrast); }
.dcms-badge[data-tone="outline"] {
  background: none;
  color: var(--dcms-color-text);
  box-shadow: inset 0 0 0 var(--dcms-border-width) var(--dcms-color-border-strong);
}

.dcms-label {
  font-size: var(--dcms-text-sm);
  font-weight: 600;
  letter-spacing: var(--dcms-tracking-label);
  text-transform: uppercase;
  color: var(--dcms-color-muted);
}

/* --- Media ----------------------------------------------------- */

.dcms-image { border-radius: var(--dcms-radius); }
.dcms-image[data-shape="circle"] { border-radius: var(--dcms-radius-pill); aspect-ratio: 1; object-fit: cover; }
.dcms-image[data-shape="square"] { aspect-ratio: 1; object-fit: cover; }
.dcms-image[data-shape="wide"] { aspect-ratio: 16 / 9; object-fit: cover; }
.dcms-image[data-shadow="true"] { box-shadow: var(--dcms-shadow-lg); }

.dcms-figure { margin: 0; }
.dcms-figure figcaption {
  color: var(--dcms-color-muted);
  font-size: var(--dcms-text-sm);
  padding-top: var(--dcms-space-xs);
}

.dcms-video {
  position: relative;
  aspect-ratio: 16 / 9;
  background: var(--dcms-color-surface-sunken);
  border-radius: var(--dcms-radius-lg);
  overflow: hidden;
  box-shadow: var(--dcms-shadow-md);
}
.dcms-video iframe,
.dcms-video video { width: 100%; height: 100%; border: 0; }

.dcms-audio { width: 100%; }

.dcms-media-gallery { display: grid; gap: var(--dcms-space-md); grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); }
.dcms-media-gallery[data-columns="2"] { grid-template-columns: repeat(2, minmax(0, 1fr)); }
.dcms-media-gallery[data-columns="3"] { grid-template-columns: repeat(3, minmax(0, 1fr)); }
.dcms-media-gallery[data-columns="4"] { grid-template-columns: repeat(4, minmax(0, 1fr)); }
.dcms-media-gallery img {
  border-radius: var(--dcms-radius);
  aspect-ratio: 1;
  object-fit: cover;
  transition: transform var(--dcms-transition), box-shadow var(--dcms-transition);
}
.dcms-media-gallery img:hover { transform: scale(1.02); box-shadow: var(--dcms-shadow-md); }

.dcms-icon { display: inline-flex; width: 1.5rem; height: 1.5rem; color: var(--dcms-color-brand); }
.dcms-icon[data-size="sm"] { width: 1rem; height: 1rem; }
.dcms-icon[data-size="lg"] { width: 2.5rem; height: 2.5rem; }
.dcms-icon svg { width: 100%; height: 100%; }
.dcms-icon[data-boxed="true"] {
  width: 3rem;
  height: 3rem;
  padding: 0.7rem;
  border-radius: var(--dcms-radius);
  background: var(--dcms-color-brand-soft);
}

.dcms-logo { display: inline-flex; align-items: center; gap: var(--dcms-space-xs); font-weight: 700; text-decoration: none; color: inherit; }
.dcms-logo img { max-height: 2.5rem; width: auto; }

/* --- Navigation ------------------------------------------------ */

.dcms-navbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--dcms-space-md);
  padding: var(--dcms-space-sm) var(--dcms-space-lg);
  border-bottom: var(--dcms-border-width) solid var(--dcms-color-border);
  background: var(--dcms-color-surface);
}
.dcms-navbar[data-layout="center"] { justify-content: center; flex-direction: column; }
.dcms-navbar[data-layout="stacked"] { flex-direction: column; align-items: flex-start; }
.dcms-navbar[data-sticky="true"] { position: sticky; top: 0; z-index: 20; box-shadow: var(--dcms-shadow-sm); }
.dcms-navbar[data-tone="inverse"] {
  background: var(--dcms-color-inverse);
  color: var(--dcms-color-inverse-text);
  border-bottom-color: transparent;
}
.dcms-navbar[data-tone="inverse"] a { color: inherit; }

.dcms-navbar-links { display: flex; align-items: center; gap: var(--dcms-space-md); }
.dcms-navbar-links a {
  color: var(--dcms-color-text);
  text-decoration: none;
  font-weight: 500;
  padding: var(--dcms-space-2xs) 0;
  border-bottom: 2px solid transparent;
  transition: border-color var(--dcms-transition), color var(--dcms-transition);
}
.dcms-navbar-links a:hover,
.dcms-navbar-links a[aria-current="page"] { border-bottom-color: var(--dcms-color-brand); color: var(--dcms-color-brand); }

.dcms-navbar-toggle { display: none; }
.dcms-navbar-burger { display: none; cursor: pointer; padding: 0.5rem; }
.dcms-navbar-burger span,
.dcms-navbar-burger span::before,
.dcms-navbar-burger span::after {
  display: block; width: 1.25rem; height: 2px; background: currentColor; content: '';
}
.dcms-navbar-burger span::before { transform: translateY(-6px); }
.dcms-navbar-burger span::after { transform: translateY(4px); }

@media (max-width: 640px) {
  .dcms-navbar { flex-wrap: wrap; }
  .dcms-navbar-burger { display: block; }
  .dcms-navbar-links { display: none; width: 100%; flex-direction: column; align-items: flex-start; }
  .dcms-navbar-toggle:checked ~ .dcms-navbar-links { display: flex; }
}

.dcms-menu { display: flex; gap: var(--dcms-space-md); list-style: none; margin: 0; padding: 0; }
.dcms-menu[data-direction="column"] { flex-direction: column; }
.dcms-menu a { color: inherit; text-decoration: none; }
.dcms-menu a:hover { color: var(--dcms-color-brand); }

.dcms-breadcrumbs { font-size: var(--dcms-text-sm); }
.dcms-breadcrumbs ol { display: flex; flex-wrap: wrap; gap: 0.5rem; list-style: none; margin: 0; padding: 0; }
.dcms-breadcrumbs li + li::before { content: '/'; padding-right: 0.5rem; color: var(--dcms-color-muted); }
.dcms-breadcrumbs a { color: var(--dcms-color-muted); text-decoration: none; }

.dcms-tabs { display: flex; flex-wrap: wrap; }
.dcms-tabs > input { position: absolute; opacity: 0; pointer-events: none; }
.dcms-tabs > label {
  order: -1;
  cursor: pointer;
  padding: var(--dcms-space-xs) var(--dcms-space-md);
  border-bottom: 2px solid var(--dcms-color-border);
  color: var(--dcms-color-muted);
  font-weight: 500;
  transition: color var(--dcms-transition), border-color var(--dcms-transition);
}
.dcms-tabs > .dcms-tab-panel { display: none; width: 100%; padding-top: var(--dcms-space-md); }
.dcms-tabs > input:checked + label { color: var(--dcms-color-brand); border-bottom-color: var(--dcms-color-brand); }
.dcms-tabs > input:checked + label + .dcms-tab-panel { display: block; }

.dcms-accordion details {
  border-bottom: var(--dcms-border-width) solid var(--dcms-color-border);
  padding: var(--dcms-space-sm) 0;
}
.dcms-accordion summary {
  cursor: pointer;
  font-weight: 600;
  font-family: var(--dcms-font-heading);
  padding: var(--dcms-space-2xs) 0;
  list-style: none;
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: var(--dcms-space-md);
}
.dcms-accordion summary::-webkit-details-marker { display: none; }
.dcms-accordion summary::after {
  content: '';
  flex: none;
  width: 0.55em;
  height: 0.55em;
  border-right: 2px solid var(--dcms-color-muted);
  border-bottom: 2px solid var(--dcms-color-muted);
  transform: rotate(45deg);
  transition: transform var(--dcms-transition);
}
.dcms-accordion details[open] summary::after { transform: rotate(-135deg); }
.dcms-accordion details > :not(summary) { padding-top: var(--dcms-space-xs); color: var(--dcms-color-muted); }

.dcms-pagination { display: flex; gap: 0.5rem; }
.dcms-pagination a {
  padding: var(--dcms-space-2xs) var(--dcms-space-sm);
  border: var(--dcms-border-width) solid var(--dcms-color-border);
  border-radius: var(--dcms-radius-sm);
  text-decoration: none;
  color: inherit;
  transition: background var(--dcms-transition), border-color var(--dcms-transition);
}
.dcms-pagination a:hover { border-color: var(--dcms-color-brand); }
.dcms-pagination a[aria-current="page"] {
  background: var(--dcms-color-brand);
  color: var(--dcms-color-brand-contrast);
  border-color: transparent;
}

.dcms-sidebar { flex: 0 0 16rem; }
.dcms-sidebar[data-side="right"] { order: 1; }

.dcms-footer {
  padding: var(--dcms-space-xl) var(--dcms-space-lg);
  border-top: var(--dcms-border-width) solid var(--dcms-color-border);
  background: var(--dcms-color-surface-alt);
}
.dcms-footer[data-tone="inverse"] {
  background: var(--dcms-color-inverse);
  color: var(--dcms-color-inverse-text);
  border-top-color: transparent;
}
.dcms-footer[data-tone="inverse"] a { color: inherit; }
.dcms-footer-note {
  color: var(--dcms-color-muted);
  font-size: var(--dcms-text-sm);
  margin: var(--dcms-space-md) 0 0;
}

/* --- Cards, the shape most sections are made of ----------------- */

.dcms-card {
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background: var(--dcms-color-surface);
  border: var(--dcms-border-width) solid var(--dcms-color-border);
  border-radius: var(--dcms-radius-lg);
  box-shadow: var(--dcms-shadow-sm);
  transition: transform var(--dcms-transition), box-shadow var(--dcms-transition), border-color var(--dcms-transition);
}
.dcms-card[data-variant="plain"] { border-color: transparent; box-shadow: none; background: none; }
.dcms-card[data-variant="outline"] { box-shadow: none; }
.dcms-card[data-variant="raised"] { border-color: transparent; box-shadow: var(--dcms-shadow-lg); }
.dcms-card[data-variant="soft"] { border-color: transparent; box-shadow: none; background: var(--dcms-color-surface-alt); }
.dcms-card[data-hover="lift"]:hover { transform: translateY(-4px); box-shadow: var(--dcms-shadow-lg); }
.dcms-card[data-hover="border"]:hover { border-color: var(--dcms-color-brand); }
.dcms-card-img { width: 100%; aspect-ratio: 16 / 9; object-fit: cover; display: block; border-radius: 0; }
.dcms-card-body { padding: var(--dcms-space-md); display: flex; flex-direction: column; gap: var(--dcms-space-xs); flex: 1; }
.dcms-card-title { margin: 0; font-size: var(--dcms-text-lg); }
.dcms-card-text { margin: 0; color: var(--dcms-color-muted); font-size: var(--dcms-text-sm); }
.dcms-card-meta {
  margin: 0;
  color: var(--dcms-color-muted);
  font-size: var(--dcms-text-xs);
  letter-spacing: var(--dcms-tracking-label);
  text-transform: uppercase;
}
.dcms-card-foot { margin-top: auto; padding-top: var(--dcms-space-xs); }
a.dcms-card-link { text-decoration: none; color: inherit; display: block; }

/* --- Sections -------------------------------------------------- */

.dcms-hero { background: var(--dcms-color-surface-alt); }
.dcms-hero[data-align="center"] { text-align: center; }
.dcms-hero[data-align="center"] .dcms-hero-text { margin-inline: auto; }
.dcms-hero[data-align="right"] { text-align: right; }
.dcms-hero-title { font-size: var(--dcms-text-5xl); margin-bottom: var(--dcms-space-md); }
.dcms-hero-text {
  max-width: var(--dcms-container-narrow);
  font-size: var(--dcms-text-lg);
  color: var(--dcms-color-muted);
  margin-bottom: var(--dcms-space-lg);
}
.dcms-hero-actions { display: flex; flex-wrap: wrap; gap: var(--dcms-space-sm); }
.dcms-hero[data-align="center"] .dcms-hero-actions { justify-content: center; }
.dcms-hero[data-align="right"] .dcms-hero-actions { justify-content: flex-end; }

.dcms-hero[data-variant="minimal"] { background: none; padding-block: var(--dcms-space-lg); }
.dcms-hero[data-variant="full-height"] { min-height: 85vh; display: flex; align-items: center; }
.dcms-hero[data-variant="full-height"] .dcms-container { width: 100%; }
.dcms-hero[data-variant="split"] { text-align: left; }
.dcms-hero[data-variant="split"] .dcms-hero-text { margin-inline: 0; }
.dcms-hero[data-variant="gradient"] {
  background: linear-gradient(135deg, var(--dcms-color-brand), var(--dcms-color-accent));
  color: var(--dcms-color-brand-contrast);
}
.dcms-hero[data-variant="gradient"] :is(h1, .dcms-hero-text) { color: inherit; }
.dcms-hero[data-variant="image-bg"] {
  background-size: cover;
  background-position: center;
  color: var(--dcms-color-inverse-text);
  isolation: isolate;
}
.dcms-hero[data-variant="image-bg"]::before {
  content: '';
  position: absolute;
  inset: 0;
  background: var(--dcms-overlay);
  z-index: -1;
}
.dcms-hero[data-variant="image-bg"] :is(h1, .dcms-hero-text) { color: inherit; }

/* The split hero shares the hero's inner classes but not its identity class —
   two components answering to the same class would make the parser guess. */
.dcms-hero-split { background: var(--dcms-color-surface-alt); }
.dcms-hero-split .dcms-hero-text { margin-inline: 0; }
.dcms-hero-split[data-reverse="true"] .dcms-row { flex-direction: row-reverse; }

.dcms-feature { display: flex; flex-direction: column; gap: var(--dcms-space-xs); }
.dcms-feature h3 { margin-bottom: 0; font-size: var(--dcms-text-lg); }
.dcms-feature p { color: var(--dcms-color-muted); margin-bottom: 0; }
.dcms-feature-grid[data-variant="cards"] .dcms-feature {
  padding: var(--dcms-space-lg);
  background: var(--dcms-color-surface);
  border: var(--dcms-border-width) solid var(--dcms-color-border);
  border-radius: var(--dcms-radius-lg);
  box-shadow: var(--dcms-shadow-sm);
}
.dcms-feature-grid[data-variant="bordered"] .dcms-feature {
  padding-left: var(--dcms-space-md);
  border-left: calc(var(--dcms-border-width) * 2) solid var(--dcms-color-brand);
}
.dcms-feature-grid[data-variant="numbered"] { counter-reset: dcms-feature; }
.dcms-feature-grid[data-variant="numbered"] .dcms-feature { counter-increment: dcms-feature; }
.dcms-feature-grid[data-variant="numbered"] .dcms-feature h3::before {
  content: counter(dcms-feature, decimal-leading-zero);
  display: block;
  font-size: var(--dcms-text-2xl);
  color: var(--dcms-color-brand);
  opacity: 0.35;
}

.dcms-feature-list .dcms-row + .dcms-row { margin-top: var(--dcms-space-xl); }
/* Alternate the image side automatically — the effect every "alternating rows"
   section wants, without the author flipping a switch per row. */
.dcms-feature-list[data-alternate="true"] .dcms-row:nth-child(even) { flex-direction: row-reverse; }

.dcms-call-to-action {
  background: var(--dcms-color-brand);
  color: var(--dcms-color-brand-contrast);
  text-align: center;
}
.dcms-call-to-action :is(h1, h2, h3, a) { color: inherit; }
.dcms-call-to-action .dcms-button {
  background: var(--dcms-color-brand-contrast);
  color: var(--dcms-color-brand);
  border-color: transparent;
}
.dcms-call-to-action[data-variant="soft"] {
  background: var(--dcms-color-brand-soft);
  color: var(--dcms-color-text);
}
.dcms-call-to-action[data-variant="soft"] :is(h1, h2, h3) { color: var(--dcms-color-heading); }
.dcms-call-to-action[data-variant="soft"] .dcms-button {
  background: var(--dcms-color-brand);
  color: var(--dcms-color-brand-contrast);
}
.dcms-call-to-action .dcms-hero-actions { justify-content: center; margin-top: var(--dcms-space-md); }
.dcms-call-to-action[data-variant="split"] { text-align: left; }
.dcms-call-to-action[data-variant="split"] .dcms-hero-actions { margin-top: 0; }
.dcms-call-to-action[data-variant="split"] .dcms-container {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: var(--dcms-space-lg);
}

.dcms-plan {
  padding: var(--dcms-space-lg);
  background: var(--dcms-color-surface);
  border: var(--dcms-border-width) solid var(--dcms-color-border);
  border-radius: var(--dcms-radius-lg);
  box-shadow: var(--dcms-shadow-sm);
  display: flex;
  flex-direction: column;
  gap: var(--dcms-space-sm);
}
.dcms-plan[data-featured="true"] {
  border-color: var(--dcms-color-brand);
  box-shadow: var(--dcms-shadow-lg);
  position: relative;
}
.dcms-plan[data-featured="true"]::after {
  content: attr(data-featured-label);
  position: absolute;
  top: 0;
  right: var(--dcms-space-md);
  transform: translateY(-50%);
  padding: 0.2em 0.7em;
  border-radius: var(--dcms-radius-pill);
  background: var(--dcms-color-brand);
  color: var(--dcms-color-brand-contrast);
  font-size: var(--dcms-text-xs);
  font-weight: 600;
  letter-spacing: var(--dcms-tracking-label);
}
.dcms-plan h3 { margin-bottom: 0; }
.dcms-plan-price { font-size: var(--dcms-text-3xl); font-weight: 700; margin: 0; color: var(--dcms-color-heading); }
.dcms-plan-price span { font-size: var(--dcms-text-sm); font-weight: 400; color: var(--dcms-color-muted); }
.dcms-plan .dcms-list { margin: 0; }
.dcms-plan .dcms-button { margin-top: auto; text-align: center; }

.dcms-person { text-align: center; }
.dcms-person img {
  border-radius: var(--dcms-radius-lg);
  aspect-ratio: 1;
  object-fit: cover;
  width: 100%;
  margin-bottom: var(--dcms-space-sm);
}
.dcms-person h3 { margin-bottom: 0; font-size: var(--dcms-text-lg); }
.dcms-person p { color: var(--dcms-color-muted); margin-bottom: 0; font-size: var(--dcms-text-sm); }
.dcms-team-grid[data-variant="round"] .dcms-person img { border-radius: var(--dcms-radius-pill); }

.dcms-stat { text-align: center; }
.dcms-stat-value {
  display: block;
  font-family: var(--dcms-font-heading);
  font-size: var(--dcms-text-3xl);
  font-weight: 700;
  line-height: 1.1;
  color: var(--dcms-color-brand);
  letter-spacing: var(--dcms-tracking-heading);
}
.dcms-stat-label {
  color: var(--dcms-color-muted);
  font-size: var(--dcms-text-sm);
  letter-spacing: var(--dcms-tracking-label);
}
.dcms-stats-band[data-variant="divided"] .dcms-stat + .dcms-stat {
  border-left: var(--dcms-border-width) solid var(--dcms-color-border);
}

.dcms-testimonial {
  display: flex;
  flex-direction: column;
  gap: var(--dcms-space-sm);
  padding: var(--dcms-space-lg);
  background: var(--dcms-color-surface);
  border: var(--dcms-border-width) solid var(--dcms-color-border);
  border-radius: var(--dcms-radius-lg);
  box-shadow: var(--dcms-shadow-sm);
}
.dcms-testimonial p { margin: 0; font-size: var(--dcms-text-lg); }
.dcms-testimonial-author { display: flex; align-items: center; gap: var(--dcms-space-sm); margin-top: auto; }
.dcms-testimonial-author img {
  width: 2.75rem;
  height: 2.75rem;
  border-radius: var(--dcms-radius-pill);
  object-fit: cover;
  flex: none;
}
.dcms-testimonial-name { font-weight: 600; display: block; }
.dcms-testimonial-role { color: var(--dcms-color-muted); font-size: var(--dcms-text-sm); }

/* A pull quote wants the band to itself and the quote centred in it. */
.dcms-quote-band { text-align: center; }
.dcms-quote-band .dcms-blockquote { border-left: 0; padding-left: 0; }

.dcms-logo-strip { text-align: center; }
.dcms-logo-strip-label {
  color: var(--dcms-color-muted);
  font-size: var(--dcms-text-sm);
  letter-spacing: var(--dcms-tracking-label);
  text-transform: uppercase;
}
.dcms-logo-strip-items {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: center;
  gap: var(--dcms-space-xl);
}
.dcms-logo-strip-items img {
  max-height: 2rem;
  width: auto;
  opacity: 0.6;
  transition: opacity var(--dcms-transition);
}
.dcms-logo-strip-items img:hover { opacity: 1; }
.dcms-logo-strip[data-variant="mono"] .dcms-logo-strip-items img { filter: grayscale(1); }

.dcms-timeline-items {
  list-style: none;
  margin: 0;
  padding: 0 0 0 var(--dcms-space-lg);
  border-left: calc(var(--dcms-border-width) * 2) solid var(--dcms-color-border);
}
.dcms-timeline-item { position: relative; padding-bottom: var(--dcms-space-lg); }
.dcms-timeline-item:last-child { padding-bottom: 0; }
.dcms-timeline-item::before {
  content: '';
  position: absolute;
  left: calc(var(--dcms-space-lg) * -1 - 5px);
  top: 0.45rem;
  width: 9px;
  height: 9px;
  border-radius: var(--dcms-radius-pill);
  background: var(--dcms-color-brand);
  box-shadow: 0 0 0 4px var(--dcms-color-surface);
}
.dcms-timeline-item time {
  display: block;
  color: var(--dcms-color-brand);
  font-size: var(--dcms-text-sm);
  font-weight: 600;
  letter-spacing: var(--dcms-tracking-label);
}
.dcms-timeline-item h3 { margin: var(--dcms-space-2xs) 0; font-size: var(--dcms-text-lg); }
.dcms-timeline-item p { color: var(--dcms-color-muted); margin-bottom: 0; }

.dcms-steps ol,
.dcms-steps .dcms-grid { list-style: none; margin: 0; padding: 0; counter-reset: dcms-step; }
.dcms-step { counter-increment: dcms-step; }
.dcms-step h3 { display: flex; align-items: center; gap: var(--dcms-space-sm); font-size: var(--dcms-text-lg); }
.dcms-step h3::before {
  content: counter(dcms-step);
  flex: none;
  display: grid;
  place-items: center;
  width: 2rem;
  height: 2rem;
  border-radius: var(--dcms-radius-pill);
  background: var(--dcms-color-brand);
  color: var(--dcms-color-brand-contrast);
  font-size: var(--dcms-text-sm);
  font-weight: 700;
}
.dcms-step p { color: var(--dcms-color-muted); margin-bottom: 0; }

.dcms-faq-item summary { font-size: var(--dcms-text-lg); }

.dcms-table { width: 100%; border-collapse: collapse; font-size: var(--dcms-text-sm); }
.dcms-table th,
.dcms-table td {
  padding: var(--dcms-space-sm) var(--dcms-space-sm);
  border-bottom: var(--dcms-border-width) solid var(--dcms-color-border);
  text-align: left;
}
.dcms-table th {
  font-family: var(--dcms-font-heading);
  font-weight: 600;
  color: var(--dcms-color-heading);
  letter-spacing: var(--dcms-tracking-label);
}
.dcms-table tbody tr:hover { background: var(--dcms-color-surface-alt); }
.dcms-table[data-variant="striped"] tbody tr:nth-child(even) { background: var(--dcms-color-surface-alt); }

.dcms-table-scroll { overflow-x: auto; }

.dcms-contact-details { font-style: normal; }
.dcms-contact-details a { text-decoration: none; }
.dcms-map-embed {
  aspect-ratio: 16 / 9;
  border-radius: var(--dcms-radius-lg);
  background: var(--dcms-color-surface-sunken);
  overflow: hidden;
}
.dcms-map-embed iframe { width: 100%; height: 100%; border: 0; }

/* --- Interactive ----------------------------------------------- */

.dcms-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: var(--dcms-space-2xs);
  padding: var(--dcms-space-sm) var(--dcms-space-lg);
  border: var(--dcms-border-width) solid transparent;
  border-radius: var(--dcms-radius);
  background: var(--dcms-color-brand);
  color: var(--dcms-color-brand-contrast);
  font-family: var(--dcms-font-body);
  font-size: var(--dcms-text-base);
  font-weight: 600;
  line-height: 1.2;
  text-decoration: none;
  cursor: pointer;
  transition: background var(--dcms-transition), color var(--dcms-transition),
    box-shadow var(--dcms-transition), transform var(--dcms-transition);
}
.dcms-button:hover { background: var(--dcms-color-brand-strong); color: var(--dcms-color-brand-contrast); box-shadow: var(--dcms-shadow-md); }
.dcms-button:active { transform: translateY(1px); }
.dcms-button[data-variant="outline"] {
  background: none;
  border-color: var(--dcms-color-brand);
  color: var(--dcms-color-brand);
}
.dcms-button[data-variant="outline"]:hover { background: var(--dcms-color-brand-soft); color: var(--dcms-color-brand-strong); }
.dcms-button[data-variant="ghost"] { background: none; color: var(--dcms-color-brand); box-shadow: none; }
.dcms-button[data-variant="ghost"]:hover { background: var(--dcms-color-brand-soft); box-shadow: none; }
.dcms-button[data-variant="soft"] { background: var(--dcms-color-brand-soft); color: var(--dcms-color-brand-strong); }
.dcms-button[data-variant="accent"] { background: var(--dcms-color-accent); color: var(--dcms-color-accent-contrast); }
.dcms-button[data-variant="link"] {
  background: none;
  color: var(--dcms-color-brand);
  padding-inline: 0;
  text-decoration: underline;
  box-shadow: none;
}
.dcms-button[data-size="sm"] { padding: var(--dcms-space-2xs) var(--dcms-space-sm); font-size: var(--dcms-text-sm); }
.dcms-button[data-size="lg"] { padding: var(--dcms-space-md) var(--dcms-space-xl); font-size: var(--dcms-text-lg); }
.dcms-button[data-shape="pill"] { border-radius: var(--dcms-radius-pill); }
.dcms-button[data-full="true"] { width: 100%; }

.dcms-button-group { display: inline-flex; flex-wrap: wrap; gap: var(--dcms-space-sm); }

.dcms-carousel {
  display: grid;
  grid-auto-flow: column;
  grid-auto-columns: 100%;
  gap: var(--dcms-space-md);
  overflow-x: auto;
  scroll-snap-type: x mandatory;
  scrollbar-width: thin;
  padding-bottom: var(--dcms-space-xs);
}
.dcms-carousel[data-per-view="2"] { grid-auto-columns: calc(50% - var(--dcms-space-md) / 2); }
.dcms-carousel[data-per-view="3"] { grid-auto-columns: calc(33.333% - var(--dcms-space-md)); }
.dcms-carousel[data-per-view="4"] { grid-auto-columns: calc(25% - var(--dcms-space-md)); }
.dcms-slide { scroll-snap-align: start; }

.dcms-modal-dialog {
  border: 0;
  border-radius: var(--dcms-radius-lg);
  padding: var(--dcms-space-lg);
  max-width: 32rem;
  box-shadow: var(--dcms-shadow-xl);
  background: var(--dcms-color-surface);
  color: var(--dcms-color-text);
}
.dcms-modal-dialog::backdrop { background: var(--dcms-overlay); }

.dcms-lightbox-full {
  display: none;
  position: fixed;
  inset: 0;
  z-index: 50;
  background: var(--dcms-overlay);
  place-items: center;
}
.dcms-lightbox-full:target { display: grid; }
.dcms-lightbox-full img { max-width: 90vw; max-height: 90vh; object-fit: contain; }

.dcms-tooltip { position: relative; border-bottom: 1px dotted currentColor; cursor: help; }
.dcms-tooltip::after {
  content: attr(data-tip);
  position: absolute;
  bottom: 100%;
  left: 50%;
  transform: translateX(-50%);
  margin-bottom: 0.4rem;
  padding: var(--dcms-space-2xs) var(--dcms-space-xs);
  white-space: nowrap;
  border-radius: var(--dcms-radius-sm);
  background: var(--dcms-color-inverse);
  color: var(--dcms-color-inverse-text);
  font-size: var(--dcms-text-sm);
  box-shadow: var(--dcms-shadow-md);
  opacity: 0;
  pointer-events: none;
  transition: opacity var(--dcms-transition);
}
.dcms-tooltip:hover::after,
.dcms-tooltip:focus::after { opacity: 1; }

.dcms-countdown {
  display: flex;
  gap: var(--dcms-space-md);
  font-family: var(--dcms-font-heading);
  font-variant-numeric: tabular-nums;
  font-size: var(--dcms-text-2xl);
  font-weight: 700;
}

.dcms-progress progress { width: 100%; height: 0.5rem; accent-color: var(--dcms-color-brand); }

.dcms-rating::before { content: '★★★★★'; letter-spacing: 0.1em; color: var(--dcms-color-brand); }

.dcms-social-links,
.dcms-share-buttons { display: flex; flex-wrap: wrap; gap: var(--dcms-space-md); }
.dcms-social-links a { color: var(--dcms-color-muted); transition: color var(--dcms-transition); }
.dcms-social-links a:hover { color: var(--dcms-color-brand); }

.dcms-cookie-banner {
  position: fixed;
  inset-inline: 0;
  bottom: 0;
  z-index: 40;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--dcms-space-md);
  padding: var(--dcms-space-md) var(--dcms-space-lg);
  background: var(--dcms-color-inverse);
  color: var(--dcms-color-inverse-text);
  box-shadow: var(--dcms-shadow-xl);
}
.dcms-cookie-banner p { margin: 0; flex: 1 1 20rem; font-size: var(--dcms-text-sm); }

/* --- Forms ----------------------------------------------------- */

.dcms-form { display: flex; flex-direction: column; gap: var(--dcms-space-md); max-width: 32rem; }
.dcms-field { display: flex; flex-direction: column; gap: 0.35rem; }
.dcms-field > span { font-size: var(--dcms-text-sm); font-weight: 600; color: var(--dcms-color-heading); }
.dcms-field input,
.dcms-field select,
.dcms-field textarea {
  padding: var(--dcms-space-sm);
  border: var(--dcms-border-width) solid var(--dcms-color-border-strong);
  border-radius: var(--dcms-radius-sm);
  background: var(--dcms-color-surface);
  color: var(--dcms-color-text);
  font-family: var(--dcms-font-body);
  font-size: var(--dcms-text-base);
  transition: border-color var(--dcms-transition), box-shadow var(--dcms-transition);
}
.dcms-field input::placeholder,
.dcms-field textarea::placeholder { color: var(--dcms-color-muted); }
.dcms-field input:focus,
.dcms-field select:focus,
.dcms-field textarea:focus {
  outline: none;
  border-color: var(--dcms-color-brand);
  box-shadow: 0 0 0 3px var(--dcms-color-brand-soft);
}
.dcms-field-checkbox { flex-direction: row; align-items: center; gap: 0.5rem; }
.dcms-field-checkbox input { accent-color: var(--dcms-color-brand); }
.dcms-field-hint { font-size: var(--dcms-text-xs); color: var(--dcms-color-muted); }
.dcms-form-radio-group {
  border: var(--dcms-border-width) solid var(--dcms-color-border);
  border-radius: var(--dcms-radius-sm);
  padding: var(--dcms-space-md);
}
.dcms-form-radio-group label { display: block; padding: 0.15rem 0; }
.dcms-newsletter-signup .dcms-form {
  flex-direction: row;
  flex-wrap: wrap;
  align-items: flex-end;
  max-width: none;
}
.dcms-newsletter-signup .dcms-field { flex: 1 1 16rem; }

/* --- Plugin content -------------------------------------------- */
/* What hydrate.js renders into a data-bound placeholder on the published page,
   and what the canvas draws in its preview. Styled here, in the site's own
   stylesheet, so plugin content follows the design kit like everything else
   and stays editable. hydrate.js ships a zero-specificity fallback for pages
   whose stylesheet predates these rules; anything here always wins. */

.dcms-collection {
  display: grid;
  gap: var(--dcms-space-md);
  grid-template-columns: repeat(auto-fill, minmax(16rem, 1fr));
  width: 100%;
}
.dcms-collection[data-columns="1"] { grid-template-columns: minmax(0, 1fr); }
.dcms-collection[data-columns="2"] { grid-template-columns: repeat(2, minmax(0, 1fr)); }
.dcms-collection[data-columns="3"] { grid-template-columns: repeat(3, minmax(0, 1fr)); }
.dcms-collection[data-columns="4"] { grid-template-columns: repeat(4, minmax(0, 1fr)); }

/* Masonry without JavaScript. Column flow only reads top-to-bottom per column,
   which is the accepted trade for a gallery. */
.dcms-collection[data-layout="masonry"] {
  display: block;
  columns: 3;
  column-gap: var(--dcms-space-md);
}
.dcms-collection[data-layout="masonry"] > * {
  break-inside: avoid;
  margin-bottom: var(--dcms-space-md);
}

.dcms-collection[data-layout="carousel"] {
  display: grid;
  grid-auto-flow: column;
  grid-auto-columns: minmax(16rem, 1fr);
  overflow-x: auto;
  scroll-snap-type: x mandatory;
  padding-bottom: var(--dcms-space-xs);
}
.dcms-collection[data-layout="carousel"] > * { scroll-snap-align: start; }

.dcms-heading { margin: 0 0 var(--dcms-space-md); font-size: var(--dcms-text-2xl); }
.dcms-collection-head {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--dcms-space-sm);
  margin-bottom: var(--dcms-space-md);
}
.dcms-collection-head .dcms-heading { margin: 0; }
.dcms-more { font-weight: 600; text-decoration: none; white-space: nowrap; }

.dcms-tile { position: relative; display: block; margin: 0; overflow: hidden; border-radius: var(--dcms-radius-lg); }
.dcms-tile img { width: 100%; height: 100%; object-fit: cover; display: block; transition: transform var(--dcms-transition); }
.dcms-tile:hover img { transform: scale(1.04); }
.dcms-tile-caption {
  position: absolute;
  inset-inline: 0;
  bottom: 0;
  padding: var(--dcms-space-md) var(--dcms-space-sm) var(--dcms-space-sm);
  background: linear-gradient(transparent, var(--dcms-overlay));
  color: var(--dcms-color-inverse-text);
  font-size: var(--dcms-text-sm);
  font-weight: 600;
}

.dcms-feature-item {
  display: grid;
  gap: var(--dcms-space-lg);
  align-items: center;
  grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr));
  padding: var(--dcms-space-lg);
  background: var(--dcms-color-surface-alt);
  border-radius: var(--dcms-radius-lg);
  margin-bottom: var(--dcms-space-md);
}
.dcms-feature-item img { width: 100%; border-radius: var(--dcms-radius); aspect-ratio: 16 / 9; object-fit: cover; }
.dcms-feature-item h3 { font-size: var(--dcms-text-2xl); }

.dcms-row {
  display: flex;
  flex-direction: column;
  gap: var(--dcms-space-2xs);
  padding-bottom: var(--dcms-space-sm);
  border-bottom: var(--dcms-border-width) solid var(--dcms-color-border);
}
.dcms-row-title { font-weight: 600; font-family: var(--dcms-font-heading); }
.dcms-row-text { color: var(--dcms-color-muted); font-size: var(--dcms-text-sm); }
.dcms-row-meta { color: var(--dcms-color-muted); font-size: var(--dcms-text-xs); letter-spacing: var(--dcms-tracking-label); }
/* .dcms-row stacks its parts; the compact variant puts a thumbnail beside them,
   so it has to undo the column direction it inherits from the base rule. */
.dcms-row-media { flex-direction: row; align-items: flex-start; gap: var(--dcms-space-md); }
.dcms-row-media > a { display: flex; gap: var(--dcms-space-md); align-items: flex-start; color: inherit; text-decoration: none; }
.dcms-row-media img { width: 6rem; flex: none; border-radius: var(--dcms-radius-sm); aspect-ratio: 4 / 3; object-fit: cover; }

.dcms-article { max-width: var(--dcms-container-narrow); margin: 0 auto; }
.dcms-article-title { font-size: var(--dcms-text-4xl); margin: 0 0 var(--dcms-space-md); }
.dcms-article-meta {
  color: var(--dcms-color-muted);
  font-size: var(--dcms-text-sm);
  letter-spacing: var(--dcms-tracking-label);
  margin-bottom: var(--dcms-space-md);
}
.dcms-article-img {
  width: 100%;
  border-radius: var(--dcms-radius-lg);
  margin: 0 0 var(--dcms-space-lg);
  display: block;
  box-shadow: var(--dcms-shadow-md);
}
.dcms-article-body { font-size: var(--dcms-text-lg); }

.dcms-media { width: 100%; border-radius: var(--dcms-radius); display: block; }

.dcms-downloads { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: var(--dcms-space-xs); }
.dcms-download a {
  display: flex;
  align-items: center;
  gap: var(--dcms-space-sm);
  padding: var(--dcms-space-sm) var(--dcms-space-md);
  border: var(--dcms-border-width) solid var(--dcms-color-border);
  border-radius: var(--dcms-radius-sm);
  color: var(--dcms-color-brand);
  text-decoration: none;
  transition: background var(--dcms-transition), border-color var(--dcms-transition);
}
.dcms-download a:hover { background: var(--dcms-color-brand-soft); border-color: var(--dcms-color-brand); }

.dcms-tags { display: flex; flex-wrap: wrap; gap: var(--dcms-space-2xs); }
.dcms-tag {
  padding: 0.15em 0.6em;
  border-radius: var(--dcms-radius-pill);
  background: var(--dcms-color-surface-sunken);
  color: var(--dcms-color-muted);
  font-size: var(--dcms-text-xs);
}

.dcms-empty,
.dcms-notice {
  color: var(--dcms-color-muted);
  font-size: var(--dcms-text-sm);
  padding: var(--dcms-space-md);
  margin: 0;
}
.dcms-notice {
  border: var(--dcms-border-width) dashed var(--dcms-color-border-strong);
  border-radius: var(--dcms-radius);
  text-align: center;
}

/* --- Utility --------------------------------------------------- */

.dcms-free-canvas { position: relative; min-height: 20rem; }
.dcms-free-canvas > * { position: absolute; }

.dcms-embed { position: relative; aspect-ratio: 16 / 9; }
.dcms-embed iframe { width: 100%; height: 100%; border: 0; border-radius: var(--dcms-radius-lg); }

.dcms-conditional[data-when="signed-in"],
.dcms-conditional[data-when="signed-out"] { display: block; }

/* --- Responsive ------------------------------------------------
   Explicit column counts have to collapse, or a four-column grid is four
   unreadable slivers on a phone. Stepping down rather than straight to one
   keeps a pair of stats or logos side by side where that still reads. */

@media (max-width: 900px) {
  .dcms-grid[data-columns="4"],
  .dcms-grid[data-columns="5"],
  .dcms-grid[data-columns="6"],
  .dcms-collection[data-columns="4"] { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .dcms-grid[data-columns="3"],
  .dcms-collection[data-columns="3"] { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .dcms-grid[data-feature="first"] > :first-child { grid-column: span 2; grid-row: auto; }
  .dcms-collection[data-layout="masonry"] { columns: 2; }
  .dcms-carousel[data-per-view="3"],
  .dcms-carousel[data-per-view="4"] { grid-auto-columns: calc(50% - var(--dcms-space-md) / 2); }
}

@media (max-width: 640px) {
  .dcms-section { padding-inline: var(--dcms-space-md); }
  .dcms-grid[data-columns],
  .dcms-collection[data-columns] { grid-template-columns: minmax(0, 1fr); }
  .dcms-grid[data-feature="first"] > :first-child { grid-column: auto; }
  .dcms-collection[data-layout="masonry"] { columns: 1; }
  .dcms-carousel[data-per-view] { grid-auto-columns: 88%; }
  .dcms-col[data-span] { flex: 1 1 100%; }
  .dcms-stats-band[data-variant="divided"] .dcms-stat + .dcms-stat { border-left: 0; }
  .dcms-feature-list[data-alternate="true"] .dcms-row:nth-child(even) { flex-direction: row; }
}

@media (prefers-reduced-motion: reduce) {
  *,
  *::before,
  *::after {
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 0.01ms !important;
    scroll-behavior: auto !important;
  }
}

@media print {
  .dcms-navbar,
  .dcms-cookie-banner,
  .dcms-share-buttons { display: none; }
  .dcms-section { padding-block: var(--dcms-space-md); }
}
`;
