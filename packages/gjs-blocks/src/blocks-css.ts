/**
 * Default styling for the block catalogue.
 *
 * This is seeded into a new site's `styles/global.css` rather than shipped as a
 * runtime stylesheet, because the file belongs to the author: they can edit any
 * of it in the code view, and a published page links one plain stylesheet with
 * no build step. The trade-off is that library changes do not reach existing
 * sites — which is the right way round for a website builder, where a silent
 * restyle of a live site would be far worse than a stale default.
 *
 * Everything is expressed against the theme tokens (`var(--dcms-*)`), so
 * changing the theme restyles the whole site rather than just new blocks.
 */
export const BLOCKS_CSS = `/* ---------------------------------------------------------------
   DCMS block library — default styles. Yours to edit.
   Everything below uses the theme variables from styles/theme.css.
   --------------------------------------------------------------- */

*,
*::before,
*::after { box-sizing: border-box; }

body {
  margin: 0;
  font-family: var(--dcms-font-body);
  color: var(--dcms-color-text);
  background: var(--dcms-color-surface);
  line-height: 1.6;
}

h1, h2, h3, h4, h5, h6 {
  font-family: var(--dcms-font-heading);
  line-height: 1.2;
  margin: 0 0 var(--dcms-space-md);
}

p { margin: 0 0 var(--dcms-space-md); }
img { max-width: 100%; height: auto; display: block; }
a { color: var(--dcms-color-brand); }

/* --- Layout ---------------------------------------------------- */

.dcms-section { padding: var(--dcms-space-xl) var(--dcms-space-lg); }
.dcms-section[data-width="full"] { padding-inline: 0; }

.dcms-container { max-width: 72rem; margin-inline: auto; }
.dcms-section[data-width="narrow"] .dcms-container { max-width: 46rem; }
.dcms-section[data-width="full"] .dcms-container { max-width: none; }

.dcms-row { display: flex; flex-wrap: wrap; gap: var(--dcms-space-md); }
.dcms-row[data-gap="none"] { gap: 0; }
.dcms-row[data-gap="sm"] { gap: var(--dcms-space-sm); }
.dcms-row[data-gap="lg"] { gap: var(--dcms-space-lg); }
.dcms-row[data-align="start"] { align-items: flex-start; }
.dcms-row[data-align="center"] { align-items: center; }
.dcms-row[data-align="end"] { align-items: flex-end; }

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
.dcms-grid[data-columns="2"] { grid-template-columns: repeat(2, minmax(0, 1fr)); }
.dcms-grid[data-columns="3"] { grid-template-columns: repeat(3, minmax(0, 1fr)); }
.dcms-grid[data-columns="4"] { grid-template-columns: repeat(4, minmax(0, 1fr)); }
.dcms-grid[data-columns="5"] { grid-template-columns: repeat(5, minmax(0, 1fr)); }
.dcms-grid[data-columns="6"] { grid-template-columns: repeat(6, minmax(0, 1fr)); }

.dcms-stack { display: flex; flex-direction: column; gap: var(--dcms-space-md); }
.dcms-stack[data-direction="row"] { flex-direction: row; flex-wrap: wrap; }
.dcms-stack[data-gap="sm"] { gap: var(--dcms-space-sm); }
.dcms-stack[data-gap="lg"] { gap: var(--dcms-space-lg); }

.dcms-spacer { height: var(--dcms-space-md); }
.dcms-spacer[data-size="sm"] { height: var(--dcms-space-sm); }
.dcms-spacer[data-size="lg"] { height: var(--dcms-space-lg); }
.dcms-spacer[data-size="xl"] { height: var(--dcms-space-xl); }

.dcms-divider { border: 0; border-top: 1px solid var(--dcms-color-border); margin: var(--dcms-space-lg) 0; }

/* --- Typography ------------------------------------------------ */

.dcms-rich-text > :last-child { margin-bottom: 0; }

.dcms-blockquote {
  margin: 0;
  padding-left: var(--dcms-space-md);
  border-left: 3px solid var(--dcms-color-brand);
}
.dcms-blockquote cite { color: var(--dcms-color-muted); font-style: normal; font-size: 0.9em; }

.dcms-list { padding-left: 1.25rem; }
.dcms-list[data-style="decimal"] { list-style: decimal; }
.dcms-list[data-style="none"] { list-style: none; padding-left: 0; }

.dcms-code-block {
  overflow-x: auto;
  padding: var(--dcms-space-md);
  border-radius: var(--dcms-radius);
  background: var(--dcms-color-surface-alt);
  font-size: 0.9em;
}

.dcms-badge {
  display: inline-block;
  padding: 0.15em 0.6em;
  border-radius: 999px;
  font-size: 0.8em;
  background: var(--dcms-color-brand);
  color: var(--dcms-color-brand-contrast);
}
.dcms-badge[data-tone="neutral"] { background: var(--dcms-color-border); color: var(--dcms-color-text); }
.dcms-badge[data-tone="success"] { background: #16a34a; color: #fff; }
.dcms-badge[data-tone="warning"] { background: #d97706; color: #fff; }
.dcms-badge[data-tone="danger"] { background: #dc2626; color: #fff; }

/* --- Media ----------------------------------------------------- */

.dcms-figure { margin: 0; }
.dcms-figure figcaption { color: var(--dcms-color-muted); font-size: 0.9em; padding-top: var(--dcms-space-sm); }

.dcms-video { position: relative; aspect-ratio: 16 / 9; background: var(--dcms-color-surface-alt); border-radius: var(--dcms-radius); overflow: hidden; }
.dcms-video iframe,
.dcms-video video { width: 100%; height: 100%; border: 0; }

.dcms-audio { width: 100%; }

.dcms-media-gallery { display: grid; gap: var(--dcms-space-md); grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); }
.dcms-media-gallery[data-columns="2"] { grid-template-columns: repeat(2, minmax(0, 1fr)); }
.dcms-media-gallery[data-columns="3"] { grid-template-columns: repeat(3, minmax(0, 1fr)); }
.dcms-media-gallery[data-columns="4"] { grid-template-columns: repeat(4, minmax(0, 1fr)); }

.dcms-icon { display: inline-flex; width: 1.5rem; height: 1.5rem; }
.dcms-icon[data-size="sm"] { width: 1rem; height: 1rem; }
.dcms-icon[data-size="lg"] { width: 2.5rem; height: 2.5rem; }
.dcms-icon svg { width: 100%; height: 100%; }

.dcms-logo { display: inline-flex; align-items: center; font-weight: 600; text-decoration: none; color: inherit; }
.dcms-logo img { max-height: 2.5rem; width: auto; }

/* --- Navigation ------------------------------------------------ */

.dcms-navbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--dcms-space-md);
  padding: var(--dcms-space-md) var(--dcms-space-lg);
  border-bottom: 1px solid var(--dcms-color-border);
  background: var(--dcms-color-surface);
}
.dcms-navbar[data-layout="center"] { justify-content: center; flex-direction: column; }
.dcms-navbar[data-layout="stacked"] { flex-direction: column; align-items: flex-start; }
.dcms-navbar[data-sticky] { position: sticky; top: 0; z-index: 20; }

.dcms-navbar-links { display: flex; gap: var(--dcms-space-md); }
.dcms-navbar-links a { color: var(--dcms-color-text); text-decoration: none; }
.dcms-navbar-burger { display: none; cursor: pointer; padding: 0.5rem; }
.dcms-navbar-burger span,
.dcms-navbar-burger span::before,
.dcms-navbar-burger span::after {
  display: block; width: 1.25rem; height: 2px; background: currentColor; content: '';
}
.dcms-navbar-burger span::before { transform: translateY(-6px); }
.dcms-navbar-burger span::after { transform: translateY(4px); }

@media (max-width: 640px) {
  .dcms-navbar-burger { display: block; }
  .dcms-navbar-links { display: none; width: 100%; flex-direction: column; }
  .dcms-navbar-toggle:checked ~ .dcms-navbar-links { display: flex; }
}

.dcms-menu { display: flex; gap: var(--dcms-space-md); }
.dcms-menu[data-direction="column"] { flex-direction: column; }
.dcms-menu a { color: inherit; text-decoration: none; }

.dcms-breadcrumbs ol { display: flex; flex-wrap: wrap; gap: 0.5rem; list-style: none; margin: 0; padding: 0; }
.dcms-breadcrumbs li + li::before { content: '/'; padding-right: 0.5rem; color: var(--dcms-color-muted); }

.dcms-tabs { display: flex; flex-wrap: wrap; }
.dcms-tabs > label {
  order: -1; cursor: pointer; padding: 0.6rem 1rem;
  border-bottom: 2px solid transparent; color: var(--dcms-color-muted);
}
.dcms-tabs > .dcms-tab-panel { display: none; width: 100%; padding-top: var(--dcms-space-md); }
.dcms-tabs > input:checked + label { color: var(--dcms-color-text); border-bottom-color: var(--dcms-color-brand); }
.dcms-tabs > input:checked + label + .dcms-tab-panel { display: block; }

.dcms-accordion details { border-bottom: 1px solid var(--dcms-color-border); padding: var(--dcms-space-sm) 0; }
.dcms-accordion summary { cursor: pointer; font-weight: 600; }

.dcms-pagination { display: flex; gap: 0.5rem; }
.dcms-pagination a {
  padding: 0.4rem 0.75rem; border: 1px solid var(--dcms-color-border);
  border-radius: var(--dcms-radius); text-decoration: none; color: inherit;
}
.dcms-pagination a[aria-current="page"] { background: var(--dcms-color-brand); color: var(--dcms-color-brand-contrast); border-color: transparent; }

.dcms-sidebar { flex: 0 0 16rem; }
.dcms-sidebar[data-side="right"] { order: 1; }

.dcms-footer {
  padding: var(--dcms-space-lg);
  border-top: 1px solid var(--dcms-color-border);
  background: var(--dcms-color-surface-alt);
}
.dcms-footer-note { color: var(--dcms-color-muted); font-size: 0.9em; margin: var(--dcms-space-md) 0 0; }

/* --- Sections -------------------------------------------------- */

.dcms-hero { background: var(--dcms-color-surface-alt); }
.dcms-hero[data-align="center"] { text-align: center; }
.dcms-hero[data-align="right"] { text-align: right; }
.dcms-hero[data-variant="full-height"] { min-height: 80vh; display: flex; align-items: center; }
.dcms-hero[data-variant="image-bg"] { background-size: cover; background-position: center; color: #fff; }
.dcms-hero[data-variant="minimal"] { background: none; padding-block: var(--dcms-space-lg); }
.dcms-hero-title { font-size: clamp(2rem, 5vw, 3.5rem); }
.dcms-hero-text { max-width: 40rem; margin-inline: auto; color: var(--dcms-color-muted); }
.dcms-hero[data-align="left"] .dcms-hero-text { margin-inline: 0; }

.dcms-feature h3,
.dcms-plan h3,
.dcms-person h3,
.dcms-step h3 { margin-bottom: var(--dcms-space-sm); }
.dcms-feature p,
.dcms-person p { color: var(--dcms-color-muted); }

.dcms-call-to-action { background: var(--dcms-color-brand); color: var(--dcms-color-brand-contrast); text-align: center; }
.dcms-call-to-action a { color: inherit; }
.dcms-call-to-action .dcms-button { background: var(--dcms-color-brand-contrast); color: var(--dcms-color-brand); }

.dcms-plan {
  padding: var(--dcms-space-lg);
  border: 1px solid var(--dcms-color-border);
  border-radius: var(--dcms-radius);
  display: flex; flex-direction: column; gap: var(--dcms-space-sm);
}
.dcms-plan[data-featured="true"] { border-color: var(--dcms-color-brand); box-shadow: 0 8px 24px rgb(0 0 0 / 8%); }
.dcms-plan-price { font-size: 2rem; font-weight: 700; margin: 0; }
.dcms-plan-price span { font-size: 0.9rem; font-weight: 400; color: var(--dcms-color-muted); }
.dcms-plan .dcms-button { margin-top: auto; text-align: center; }

.dcms-person img { border-radius: var(--dcms-radius); aspect-ratio: 1; object-fit: cover; }

.dcms-stat { text-align: center; }
.dcms-stat-value { display: block; font-size: 2.25rem; font-weight: 700; line-height: 1.1; }
.dcms-stat-label { color: var(--dcms-color-muted); font-size: 0.9em; }

.dcms-logo-strip { text-align: center; }
.dcms-logo-strip-label { color: var(--dcms-color-muted); font-size: 0.85em; letter-spacing: 0.08em; text-transform: uppercase; }
.dcms-logo-strip-items { display: flex; flex-wrap: wrap; align-items: center; justify-content: center; gap: var(--dcms-space-lg); }
.dcms-logo-strip-items img { max-height: 2rem; width: auto; opacity: 0.7; }

.dcms-timeline-items { list-style: none; margin: 0; padding: 0 0 0 var(--dcms-space-lg); border-left: 2px solid var(--dcms-color-border); }
.dcms-timeline-items li { position: relative; padding-bottom: var(--dcms-space-lg); }
.dcms-timeline-items li::before {
  content: ''; position: absolute; left: calc(var(--dcms-space-lg) * -1 - 5px); top: 0.4rem;
  width: 8px; height: 8px; border-radius: 50%; background: var(--dcms-color-brand);
}
.dcms-timeline-items time { color: var(--dcms-color-muted); font-size: 0.85em; }

.dcms-steps ol,
.dcms-steps .dcms-grid { list-style: none; margin: 0; padding: 0; counter-reset: dcms-step; }
.dcms-step { counter-increment: dcms-step; }
.dcms-step h3::before { content: counter(dcms-step) '. '; color: var(--dcms-color-brand); }

.dcms-table { width: 100%; border-collapse: collapse; }
.dcms-table th,
.dcms-table td { padding: 0.6rem 0.75rem; border-bottom: 1px solid var(--dcms-color-border); text-align: left; }
.dcms-table th { font-weight: 600; }

.dcms-contact-details { font-style: normal; }
.dcms-map-embed { aspect-ratio: 16 / 9; border-radius: var(--dcms-radius); background: var(--dcms-color-surface-alt); }
.dcms-map-embed iframe { width: 100%; height: 100%; border: 0; }

/* --- Interactive ----------------------------------------------- */

.dcms-button {
  display: inline-block;
  padding: 0.75rem 1.5rem;
  border: 1px solid transparent;
  border-radius: var(--dcms-radius);
  background: var(--dcms-color-brand);
  color: var(--dcms-color-brand-contrast);
  font: inherit;
  text-decoration: none;
  cursor: pointer;
}
.dcms-button[data-variant="outline"] { background: none; border-color: var(--dcms-color-brand); color: var(--dcms-color-brand); }
.dcms-button[data-variant="ghost"] { background: none; color: var(--dcms-color-brand); }
.dcms-button[data-size="sm"] { padding: 0.4rem 0.9rem; font-size: 0.9em; }
.dcms-button[data-size="lg"] { padding: 1rem 2rem; font-size: 1.1em; }

.dcms-button-group { display: inline-flex; flex-wrap: wrap; gap: var(--dcms-space-sm); }

.dcms-carousel {
  display: grid;
  grid-auto-flow: column;
  grid-auto-columns: 100%;
  gap: var(--dcms-space-md);
  overflow-x: auto;
  scroll-snap-type: x mandatory;
}
.dcms-carousel[data-per-view="2"] { grid-auto-columns: calc(50% - var(--dcms-space-md) / 2); }
.dcms-carousel[data-per-view="3"] { grid-auto-columns: calc(33.333% - var(--dcms-space-md)); }
.dcms-slide { scroll-snap-align: start; }

.dcms-modal-dialog { border: 0; border-radius: var(--dcms-radius); padding: var(--dcms-space-lg); max-width: 32rem; }
.dcms-modal-dialog::backdrop { background: rgb(0 0 0 / 45%); }
.dcms-modal-dialog:target,
.dcms-modal-dialog[open] { display: block; }

.dcms-lightbox-full { display: none; position: fixed; inset: 0; z-index: 50; background: rgb(0 0 0 / 85%); place-items: center; }
.dcms-lightbox-full:target { display: grid; }
.dcms-lightbox-full img { max-width: 90vw; max-height: 90vh; object-fit: contain; }

.dcms-tooltip { position: relative; border-bottom: 1px dotted currentColor; cursor: help; }
.dcms-tooltip::after {
  content: attr(data-tip);
  position: absolute; bottom: 100%; left: 50%; transform: translateX(-50%);
  margin-bottom: 0.4rem; padding: 0.3rem 0.6rem; white-space: nowrap;
  border-radius: var(--dcms-radius); background: var(--dcms-color-text); color: var(--dcms-color-surface);
  font-size: 0.85em; opacity: 0; pointer-events: none; transition: opacity 120ms;
}
.dcms-tooltip:hover::after,
.dcms-tooltip:focus::after { opacity: 1; }

.dcms-countdown { display: flex; gap: var(--dcms-space-md); font-variant-numeric: tabular-nums; font-size: 1.5rem; }

.dcms-progress progress { width: 100%; height: 0.5rem; }

.dcms-rating::before {
  content: '★★★★★';
  letter-spacing: 0.1em;
  color: var(--dcms-color-brand);
}

.dcms-social-links,
.dcms-share-buttons { display: flex; flex-wrap: wrap; gap: var(--dcms-space-md); }

.dcms-cookie-banner {
  position: fixed; inset-inline: 0; bottom: 0; z-index: 40;
  display: flex; flex-wrap: wrap; align-items: center; gap: var(--dcms-space-md);
  padding: var(--dcms-space-md) var(--dcms-space-lg);
  background: var(--dcms-color-text); color: var(--dcms-color-surface);
}
.dcms-cookie-banner p { margin: 0; flex: 1 1 20rem; }

/* --- Forms ----------------------------------------------------- */

.dcms-form { display: flex; flex-direction: column; gap: var(--dcms-space-md); max-width: 32rem; }
.dcms-field { display: flex; flex-direction: column; gap: 0.35rem; }
.dcms-field > span { font-size: 0.9em; font-weight: 500; }
.dcms-field input,
.dcms-field select,
.dcms-field textarea {
  padding: 0.6rem 0.75rem;
  border: 1px solid var(--dcms-color-border);
  border-radius: var(--dcms-radius);
  background: var(--dcms-color-surface);
  color: inherit;
  font: inherit;
}
.dcms-field input:focus-visible,
.dcms-field select:focus-visible,
.dcms-field textarea:focus-visible { outline: 2px solid var(--dcms-color-brand); outline-offset: 1px; }
.dcms-field-checkbox { flex-direction: row; align-items: center; gap: 0.5rem; }
.dcms-form-radio-group { border: 1px solid var(--dcms-color-border); border-radius: var(--dcms-radius); padding: var(--dcms-space-md); }
.dcms-form-radio-group label { display: block; padding: 0.15rem 0; }
.dcms-newsletter-signup .dcms-form { flex-direction: row; flex-wrap: wrap; align-items: flex-end; max-width: none; }

/* --- Utility --------------------------------------------------- */

.dcms-free-canvas { position: relative; min-height: 20rem; }
.dcms-free-canvas > * { position: absolute; }

.dcms-embed { position: relative; aspect-ratio: 16 / 9; }
.dcms-embed iframe { width: 100%; height: 100%; border: 0; border-radius: var(--dcms-radius); }

.dcms-conditional[data-when="signed-in"],
.dcms-conditional[data-when="signed-out"] { display: block; }
`;
