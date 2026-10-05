/**
 * The built-in components' stylesheet.
 *
 * A string rather than a .css import so the same bytes can be put into the builder's canvas
 * frame and into the published site without a bundler in between. Every value reads a theme
 * token (`theme.ts`), with a fallback so a kit that leaves one out degrades instead of
 * collapsing.
 *
 * No child combinators across `.dcms-node` / `.dcms-slot`: the DOM is the same on the canvas
 * and the site (see `render.tsx`), and those wrappers sit between every component and its
 * children.
 */

/**
 * The widths the tablet and mobile overrides apply below. The builder's device widths sit inside
 * them, so switching the canvas to Tablet shows exactly what a tablet gets: the canvas frame is
 * the viewport its media queries see.
 */
export const TABLET_MAX_WIDTH = 1024;
export const MOBILE_MAX_WIDTH = 640;

/**
 * Classes a prop with `responsive: true` can take a per-device value of. Each is emitted three
 * times: as itself, as `t-<class>` under the tablet query and as `m-<class>` under the mobile
 * one, in that order — so a mobile override beats a tablet one, and a tablet override carries
 * down to phones unless mobile says otherwise.
 */
const RESPONSIVE_RULES: readonly (readonly [string, string])[] = [
  ['dcms-width-narrow', 'max-width: var(--dcms-container-narrow, 44rem);'],
  ['dcms-width-normal', 'max-width: var(--dcms-container, 72rem);'],
  ['dcms-width-wide', 'max-width: calc(var(--dcms-container, 72rem) * 1.25);'],
  ['dcms-width-full', 'max-width: none;'],
  ['dcms-py-none', 'padding-block: 0;'],
  ['dcms-py-sm', 'padding-block: calc(var(--dcms-section-py, 5rem) * 0.4);'],
  ['dcms-py-md', 'padding-block: var(--dcms-section-py, 5rem);'],
  ['dcms-py-lg', 'padding-block: calc(var(--dcms-section-py, 5rem) * 1.6);'],
  ['dcms-stack-vertical', 'flex-direction: column;'],
  ['dcms-stack-horizontal', 'flex-direction: row;'],
  ['dcms-gap-none', 'gap: 0;'],
  ['dcms-gap-xs', 'gap: var(--dcms-space-xs, 0.5rem);'],
  ['dcms-gap-sm', 'gap: var(--dcms-space-sm, 0.75rem);'],
  ['dcms-gap-md', 'gap: var(--dcms-space-md, 1rem);'],
  ['dcms-gap-lg', 'gap: var(--dcms-space-lg, 2rem);'],
  ['dcms-gap-xl', 'gap: var(--dcms-space-xl, 4rem);'],
  ['dcms-align-start', 'align-items: flex-start;'],
  ['dcms-align-center', 'align-items: center;'],
  ['dcms-align-end', 'align-items: flex-end;'],
  ['dcms-align-stretch', 'align-items: stretch;'],
  ['dcms-justify-start', 'justify-content: flex-start;'],
  ['dcms-justify-center', 'justify-content: center;'],
  ['dcms-justify-end', 'justify-content: flex-end;'],
  ['dcms-justify-between', 'justify-content: space-between;'],
  ['dcms-size-sm', 'font-size: var(--dcms-text-sm, 0.875rem);'],
  ['dcms-size-base', 'font-size: var(--dcms-text-base, 1rem);'],
  ['dcms-size-lg', 'font-size: var(--dcms-text-lg, 1.25rem);'],
  ['dcms-text-start', 'text-align: start;'],
  ['dcms-text-center', 'text-align: center;'],
  ['dcms-text-end', 'text-align: end;'],
  ['dcms-ratio-auto', 'aspect-ratio: auto;'],
  ['dcms-ratio-16-9', 'aspect-ratio: 16 / 9; height: auto;'],
  ['dcms-ratio-4-3', 'aspect-ratio: 4 / 3; height: auto;'],
  ['dcms-ratio-1-1', 'aspect-ratio: 1 / 1; height: auto;'],
  ['dcms-ratio-3-4', 'aspect-ratio: 3 / 4; height: auto;'],
  ['dcms-cols-1', 'grid-template-columns: repeat(1, minmax(0, 1fr));'],
  ['dcms-cols-2', 'grid-template-columns: repeat(2, minmax(0, 1fr));'],
  ['dcms-cols-3', 'grid-template-columns: repeat(3, minmax(0, 1fr));'],
  ['dcms-cols-4', 'grid-template-columns: repeat(4, minmax(0, 1fr));'],
  ['dcms-cols-5', 'grid-template-columns: repeat(5, minmax(0, 1fr));'],
  ['dcms-cols-6', 'grid-template-columns: repeat(6, minmax(0, 1fr));'],
  ['dcms-split-1-1', 'grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);'],
  ['dcms-split-2-1', 'grid-template-columns: minmax(0, 2fr) minmax(0, 1fr);'],
  ['dcms-split-1-2', 'grid-template-columns: minmax(0, 1fr) minmax(0, 2fr);'],
  ['dcms-split-stacked', 'grid-template-columns: minmax(0, 1fr);'],
  ['dcms-spacer-xs', 'height: var(--dcms-space-xs, 0.5rem);'],
  ['dcms-spacer-sm', 'height: var(--dcms-space-sm, 0.75rem);'],
  ['dcms-spacer-md', 'height: var(--dcms-space-md, 1rem);'],
  ['dcms-spacer-lg', 'height: var(--dcms-space-lg, 2rem);'],
  ['dcms-spacer-xl', 'height: var(--dcms-space-xl, 4rem);'],
  ['dcms-icon-sm', '--dcms-icon-size: 1rem;'],
  ['dcms-icon-md', '--dcms-icon-size: 1.5rem;'],
  ['dcms-icon-lg', '--dcms-icon-size: 2.25rem;'],
  ['dcms-icon-xl', '--dcms-icon-size: 3.5rem;'],
];

const STATIC_CSS = `
html { background: var(--dcms-color-surface, #fff); }
body { margin: 0; }
.dcms-node { min-width: 0; }
.dcms-page { display: block; min-height: 100%; }
.dcms-page, .dcms-text, .dcms-button {
  font-family: var(--dcms-font-body, system-ui, sans-serif);
  line-height: var(--dcms-leading-body, 1.6);
  color: var(--dcms-color-text, inherit);
}

.dcms-flow > * + * { margin-top: var(--dcms-space-md, 1rem); }

.dcms-width { box-sizing: border-box; margin-inline: auto; padding-inline: var(--dcms-space-md, 1rem); }

.dcms-section { display: block; }
.dcms-bg-alt { background: var(--dcms-color-surface-alt, #f8fafc); }
.dcms-bg-soft { background: var(--dcms-color-brand-soft, #eef2ff); }
.dcms-bg-inverse { background: var(--dcms-color-inverse, #0f172a); color: var(--dcms-color-inverse-text, #f8fafc); }
.dcms-bg-inverse .dcms-heading, .dcms-bg-inverse .dcms-text { color: inherit; }

.dcms-stack { display: flex; }
.dcms-wrap { flex-wrap: wrap; }

.dcms-heading {
  margin: 0;
  font-family: var(--dcms-font-heading, inherit);
  line-height: var(--dcms-leading-heading, 1.15);
  font-weight: var(--dcms-weight-heading, 650);
  letter-spacing: var(--dcms-tracking-heading, normal);
  text-transform: var(--dcms-transform-heading, none);
  color: var(--dcms-color-heading, inherit);
}
.dcms-heading-1 { font-size: var(--dcms-text-4xl, 2.75rem); }
.dcms-heading-2 { font-size: var(--dcms-text-3xl, 2.25rem); }
.dcms-heading-3 { font-size: var(--dcms-text-2xl, 1.75rem); }
.dcms-heading-4 { font-size: var(--dcms-text-xl, 1.5rem); }
.dcms-heading-5 { font-size: var(--dcms-text-lg, 1.25rem); }
.dcms-heading-6 { font-size: var(--dcms-text-base, 1rem); }

.dcms-text { margin: 0; white-space: pre-line; }
.dcms-tone-muted { color: var(--dcms-color-muted, #64748b); }

.dcms-image { display: block; width: 100%; height: auto; }
.dcms-fit-cover { object-fit: cover; }
.dcms-fit-contain { object-fit: contain; }
.dcms-radius-sm { border-radius: var(--dcms-radius-sm, 0.375rem); }
.dcms-radius-lg { border-radius: var(--dcms-radius-lg, 1rem); }
.dcms-image-empty {
  display: grid; place-items: center; min-height: 8rem;
  background: var(--dcms-color-surface-sunken, #f1f5f9); color: var(--dcms-color-muted, #64748b);
  font: 0.875rem system-ui, sans-serif;
}

.dcms-button {
  display: inline-flex; align-items: center; justify-content: center;
  border: var(--dcms-border-width, 1px) solid transparent;
  border-radius: var(--dcms-radius, 0.5rem);
  font-weight: 600; text-decoration: none; cursor: pointer;
  transition: background var(--dcms-transition, 160ms), color var(--dcms-transition, 160ms);
}
.dcms-button-sm { padding: 0.375rem 0.75rem; font-size: var(--dcms-text-sm, 0.875rem); }
.dcms-button-md { padding: 0.625rem 1.125rem; font-size: var(--dcms-text-base, 1rem); }
.dcms-button-lg { padding: 0.875rem 1.5rem; font-size: var(--dcms-text-lg, 1.125rem); }
.dcms-button-primary { background: var(--dcms-color-brand, #4f46e5); color: var(--dcms-color-brand-contrast, #fff); }
.dcms-button-primary:hover { background: var(--dcms-color-brand-strong, #4338ca); }
.dcms-button-secondary { background: transparent; color: var(--dcms-color-brand, #4f46e5); border-color: currentColor; }
.dcms-button-ghost { background: transparent; color: var(--dcms-color-brand, #4f46e5); }
.dcms-button:focus-visible { outline: 2px solid var(--dcms-color-accent, #0ea5e9); outline-offset: 2px; }

.dcms-nav-list { list-style: none; margin: 0; padding: 0; display: flex; gap: var(--dcms-space-md, 1rem); }
.dcms-nav-vertical .dcms-nav-list { flex-direction: column; gap: var(--dcms-space-xs, 0.5rem); }
.dcms-nav-list .dcms-nav-list { display: none; }
.dcms-nav-link { color: inherit; text-decoration: none; font-weight: 500; }
.dcms-nav-link:hover { color: var(--dcms-color-brand, #4f46e5); }
.dcms-outlet-placeholder {
  display: grid; place-items: center; min-height: 6rem; padding: 1rem;
  border: 1px dashed rgba(100,116,139,.6); border-radius: 0.375rem;
  color: var(--dcms-color-muted, #64748b); font: 0.875rem system-ui, sans-serif;
}
.dcms-not-found { padding: var(--dcms-section-py, 5rem) var(--dcms-space-md, 1rem); text-align: center; }

.dcms-grid { display: grid; }
.dcms-split { display: grid; gap: var(--dcms-space-lg, 2rem); align-items: start; }
.dcms-spacer { display: block; }

.dcms-collection-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(16rem, 100%), 1fr)); gap: var(--dcms-space-lg, 2rem); }
.dcms-collection-more { display: flex; justify-content: center; margin-top: var(--dcms-space-lg, 2rem); }
.dcms-pager { display: flex; align-items: center; justify-content: center; gap: var(--dcms-space-md, 1rem); margin-top: var(--dcms-space-lg, 2rem); }
.dcms-collection-list { display: flex; flex-direction: column; gap: var(--dcms-space-md, 1rem); }
.dcms-code-placeholder { font: 0.875rem system-ui, sans-serif; color: #334155; background: repeating-linear-gradient(135deg, #f8fafc 0 10px, #f1f5f9 10px 20px); border: 1px dashed #94a3b8; border-radius: 6px; padding: 1rem; }
.dcms-editor-note, .dcms-editor-state { font: 0.75rem system-ui, sans-serif; color: #64748b; padding: 0.25rem 0; }
.dcms-editor-state { margin-top: 0.75rem; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; }
.dcms-richtext > :first-child { margin-top: 0; }
.dcms-richtext img { max-width: 100%; height: auto; }
.dcms-form { display: flex; flex-direction: column; gap: var(--dcms-space-md, 1rem); align-items: flex-start; }
.dcms-form-fields { display: flex; flex-direction: column; gap: var(--dcms-space-sm, 0.75rem); align-self: stretch; }
.dcms-field { display: flex; flex-direction: column; gap: 0.25rem; font-family: var(--dcms-font-body, inherit); }
.dcms-field input, .dcms-field textarea {
  font: inherit; padding: 0.5rem 0.75rem; border: var(--dcms-border-width, 1px) solid var(--dcms-color-border, #cbd5e1);
  border-radius: var(--dcms-radius-sm, 0.375rem); background: var(--dcms-color-surface, #fff); color: inherit;
}
.dcms-field-check { flex-direction: row; align-items: center; gap: 0.5rem; }
.dcms-form-error { color: var(--dcms-color-danger, #dc2626); }
.dcms-modal-backdrop { position: fixed; inset: 0; z-index: 50; display: grid; place-items: center; padding: 1rem; background: rgba(15, 23, 42, 0.55); }
.dcms-modal-scrim { position: absolute; inset: 0; border: 0; background: transparent; cursor: default; }
.dcms-modal { position: relative; width: min(36rem, 100%); max-height: 90vh; overflow: auto; padding: var(--dcms-space-lg, 2rem); border-radius: var(--dcms-radius-lg, 1rem); background: var(--dcms-color-surface, #fff); box-shadow: var(--dcms-shadow-lg, 0 20px 40px rgba(0,0,0,.2)); }
.dcms-modal-close { position: absolute; top: 0.5rem; right: 0.75rem; border: 0; background: none; font-size: 1.5rem; line-height: 1; cursor: pointer; color: inherit; }
.dcms-modal-editor { border: 1px dashed rgba(100,116,139,.6); border-radius: 0.5rem; padding: 0.75rem; }
.dcms-toasts { position: fixed; right: 1rem; bottom: 1rem; z-index: 60; display: flex; flex-direction: column; gap: 0.5rem; }
.dcms-toast { padding: 0.75rem 1rem; border-radius: var(--dcms-radius-sm, 0.375rem); background: var(--dcms-color-inverse, #0f172a); color: var(--dcms-color-inverse-text, #fff); font: 0.875rem system-ui, sans-serif; box-shadow: 0 8px 24px rgba(0,0,0,.2); }
.dcms-toast-success { background: var(--dcms-color-success, #16a34a); }
.dcms-toast-error { background: var(--dcms-color-danger, #dc2626); }

/* Content primitives (U1.1) */
.dcms-pad-sm { padding: var(--dcms-space-sm, 0.75rem); }
.dcms-pad-md { padding: var(--dcms-space-md, 1rem) var(--dcms-space-lg, 1.5rem); }
.dcms-pad-lg { padding: var(--dcms-space-lg, 2rem); }
.dcms-card { position: relative; display: flex; flex-direction: column; overflow: hidden; height: 100%; border-radius: var(--dcms-radius, 0.625rem); background: var(--dcms-color-surface, #fff); color: var(--dcms-color-text, inherit); }
.dcms-card-outline { border: var(--dcms-border-width, 1px) solid var(--dcms-color-border, #e2e8f0); }
.dcms-card-raised { box-shadow: var(--dcms-shadow-md, 0 4px 16px rgb(15 23 42 / 10%)); }
.dcms-card-filled { background: var(--dcms-color-surface-alt, #f8fafc); }
.dcms-card-plain { background: transparent; border-radius: 0; }
.dcms-card-media img { display: block; width: 100%; }
.dcms-card-body { flex: 1; }
.dcms-card-content { display: flex; flex-direction: column; gap: var(--dcms-space-sm, 0.75rem); }
.dcms-card-footer { display: flex; flex-wrap: wrap; gap: var(--dcms-space-sm, 0.75rem); padding-top: 0; }
.dcms-card-linked { transition: transform var(--dcms-transition, 160ms ease), box-shadow var(--dcms-transition, 160ms ease); }
.dcms-card-linked:hover { transform: translateY(-2px); box-shadow: var(--dcms-shadow-lg, 0 12px 32px rgb(15 23 42 / 14%)); }
.dcms-card-cover { position: absolute; inset: 0; z-index: 0; }
.dcms-card-cover:focus-visible { outline: 2px solid var(--dcms-color-brand, #4f46e5); outline-offset: -2px; }
.dcms-card-linked .dcms-button { position: relative; z-index: 1; }
.dcms-icon { display: inline-flex; align-items: center; justify-content: center; line-height: 0; --dcms-icon-size: 1.5rem; }
.dcms-icon svg { width: var(--dcms-icon-size); height: var(--dcms-icon-size); }
.dcms-icon-tone-brand { color: var(--dcms-color-brand, #4f46e5); }
.dcms-icon-tone-muted { color: var(--dcms-color-muted, #64748b); }
.dcms-icon-shape-circle, .dcms-icon-shape-square { padding: calc(var(--dcms-icon-size) * 0.45); background: var(--dcms-color-brand-soft, #eef2ff); }
.dcms-icon-shape-circle { border-radius: 999px; }
.dcms-icon-shape-square { border-radius: var(--dcms-radius, 0.625rem); }
.dcms-badge { display: inline-flex; align-items: center; padding: 0.15em 0.65em; border-radius: var(--dcms-radius-pill, 999px); font-size: var(--dcms-text-sm, 0.875rem); font-weight: 600; letter-spacing: var(--dcms-tracking-label, 0.02em); line-height: 1.5; width: fit-content; }
.dcms-badge-brand { background: var(--dcms-color-brand-soft, #eef2ff); color: var(--dcms-color-brand-strong, #4338ca); }
.dcms-badge-neutral { background: var(--dcms-color-surface-sunken, #f1f5f9); color: var(--dcms-color-text, #1e293b); }
.dcms-badge-success { background: color-mix(in srgb, var(--dcms-color-success, #16a34a) 15%, transparent); color: var(--dcms-color-success, #16a34a); }
.dcms-badge-warning { background: color-mix(in srgb, var(--dcms-color-warning, #d97706) 15%, transparent); color: var(--dcms-color-warning, #d97706); }
.dcms-badge-danger { background: color-mix(in srgb, var(--dcms-color-danger, #dc2626) 15%, transparent); color: var(--dcms-color-danger, #dc2626); }
.dcms-list { margin: 0; padding-left: 1.4em; display: flex; flex-direction: column; font-family: var(--dcms-font-body, inherit); line-height: var(--dcms-leading-body, 1.6); }
.dcms-list-gap-sm { gap: var(--dcms-space-xs, 0.5rem); }
.dcms-list-gap-md { gap: var(--dcms-space-sm, 0.75rem); }
.dcms-list-gap-lg { gap: var(--dcms-space-md, 1rem); }
.dcms-list-check, .dcms-list-icon, .dcms-list-none { list-style: none; padding-left: 0; }
.dcms-list-check li, .dcms-list-icon li { display: flex; gap: 0.6em; align-items: flex-start; }
.dcms-list-marker { flex: none; width: 1.15em; height: 1.15em; margin-top: 0.2em; color: var(--dcms-color-brand, #4f46e5); }
.dcms-divider { border: 0; border-top: var(--dcms-border-width, 1px) solid var(--dcms-color-border, #e2e8f0); width: 100%; margin-inline: 0; }
.dcms-divider-dashed { border-top-style: dashed; }
.dcms-divider-dotted { border-top-style: dotted; border-top-width: 3px; }
.dcms-divider-short { width: 4rem; border-top-width: 3px; border-top-color: var(--dcms-color-brand, #4f46e5); }
.dcms-divider-space-sm { margin-block: var(--dcms-space-sm, 0.75rem); }
.dcms-divider-space-md { margin-block: var(--dcms-space-lg, 2rem); }
.dcms-divider-space-lg { margin-block: var(--dcms-space-xl, 4rem); }
.dcms-quote { margin: 0; }
.dcms-quote blockquote { margin: 0; }
.dcms-quote p { margin: 0; font-family: var(--dcms-font-heading, inherit); color: var(--dcms-color-heading, inherit); }
.dcms-quote p::before { content: '“'; }
.dcms-quote p::after { content: '”'; }
.dcms-quote figcaption { margin-top: var(--dcms-space-sm, 0.75rem); color: var(--dcms-color-muted, #64748b); font-size: var(--dcms-text-sm, 0.875rem); }
.dcms-quote figcaption::before { content: '— '; }
.dcms-quote-plain p { font-size: var(--dcms-text-lg, 1.25rem); font-style: italic; }
.dcms-quote-large p { font-size: var(--dcms-text-2xl, 1.75rem); line-height: 1.3; }
.dcms-quote-card { padding: var(--dcms-space-lg, 2rem); border-radius: var(--dcms-radius, 0.625rem); background: var(--dcms-color-surface-alt, #f8fafc); }
.dcms-quote-card p { font-size: var(--dcms-text-lg, 1.25rem); }

/* Media primitives (U1.2) */
.dcms-video { display: block; width: 100%; height: auto; border: 0; border-radius: var(--dcms-radius, 0.625rem); background: #000; }
.dcms-video-still { position: relative; background-size: cover; background-position: center; background-color: var(--dcms-color-surface-sunken, #f1f5f9); }
.dcms-video-play { position: absolute; left: 50%; top: 50%; transform: translate(-50%, -50%); width: 3.5rem; height: 3.5rem; border-radius: 999px; display: grid; place-items: center; background: rgb(0 0 0 / 60%); color: #fff; font-size: 1.25rem; }
.dcms-video-empty, .dcms-embed-empty { display: grid; place-items: center; padding: 1rem; text-align: center; background: var(--dcms-color-surface-sunken, #f1f5f9); color: var(--dcms-color-muted, #64748b); font: 0.875rem system-ui, sans-serif; }
.dcms-ratio-9-16 { aspect-ratio: 9 / 16; height: auto; max-height: 80vh; }
.dcms-gallery-square .dcms-image img, .dcms-gallery-square img { aspect-ratio: 1 / 1; object-fit: cover; height: auto; }
.dcms-gallery-landscape .dcms-image img, .dcms-gallery-landscape img { aspect-ratio: 4 / 3; object-fit: cover; height: auto; }
.dcms-gallery-zoomable img { cursor: zoom-in; transition: opacity var(--dcms-transition, 160ms ease); }
.dcms-gallery-zoomable img:hover { opacity: 0.88; }
.dcms-lightbox { position: fixed; inset: 0; z-index: 70; display: grid; place-items: center; padding: 3rem 4rem; }
.dcms-lightbox-scrim { position: absolute; inset: 0; border: 0; background: rgb(0 0 0 / 88%); cursor: zoom-out; }
.dcms-lightbox img { position: relative; max-width: 100%; max-height: 100%; object-fit: contain; border-radius: var(--dcms-radius-sm, 0.375rem); }
.dcms-lightbox-close, .dcms-lightbox-prev, .dcms-lightbox-next { position: absolute; border: 0; border-radius: 999px; width: 2.75rem; height: 2.75rem; background: rgb(255 255 255 / 14%); color: #fff; font-size: 1.75rem; line-height: 1; cursor: pointer; }
.dcms-lightbox-close:focus-visible, .dcms-lightbox-prev:focus-visible, .dcms-lightbox-next:focus-visible { outline: 2px solid #fff; }
.dcms-lightbox-close { top: 1rem; right: 1rem; }
.dcms-lightbox-prev { left: 1rem; top: 50%; transform: translateY(-50%); }
.dcms-lightbox-next { right: 1rem; top: 50%; transform: translateY(-50%); }
.dcms-lightbox-count { position: absolute; bottom: 1rem; left: 50%; transform: translateX(-50%); color: rgb(255 255 255 / 80%); font: 0.875rem system-ui, sans-serif; }
.dcms-map iframe, .dcms-embed iframe { display: block; width: 100%; height: 100%; border: 0; border-radius: var(--dcms-radius, 0.625rem); }
.dcms-map-sm, .dcms-embed-sm { height: 15rem; }
.dcms-map-md, .dcms-embed-md { height: 22rem; }
.dcms-map-lg, .dcms-embed-lg { height: 30rem; }
.dcms-embed-xl { height: 40rem; }
.dcms-map { display: flex; flex-direction: column; }
.dcms-map iframe { flex: 1; min-height: 0; }
.dcms-map-link { margin-top: var(--dcms-space-xs, 0.5rem); font-size: var(--dcms-text-sm, 0.875rem); color: var(--dcms-color-brand, #4f46e5); }

/* Interactive primitives (U1.3) */
.dcms-accordion-items { display: flex; flex-direction: column; }
.dcms-accordion-item summary { display: flex; align-items: center; justify-content: space-between; gap: 1rem; padding: var(--dcms-space-md, 1rem) 0; cursor: pointer; list-style: none; font-family: var(--dcms-font-heading, inherit); font-weight: var(--dcms-weight-heading, 600); color: var(--dcms-color-heading, inherit); font-size: var(--dcms-text-lg, 1.125rem); }
.dcms-accordion-item summary::-webkit-details-marker { display: none; }
.dcms-accordion-item summary:focus-visible { outline: 2px solid var(--dcms-color-brand, #4f46e5); outline-offset: 2px; }
.dcms-accordion-chevron { flex: none; width: 1.25rem; height: 1.25rem; color: var(--dcms-color-muted, #64748b); transition: transform var(--dcms-transition, 160ms ease); }
.dcms-accordion-item[open] .dcms-accordion-chevron { transform: rotate(180deg); }
.dcms-accordion-body { padding-bottom: var(--dcms-space-md, 1rem); display: flex; flex-direction: column; gap: var(--dcms-space-sm, 0.75rem); }
.dcms-accordion-lines .dcms-accordion-item { border-bottom: var(--dcms-border-width, 1px) solid var(--dcms-color-border, #e2e8f0); }
.dcms-accordion-boxed .dcms-accordion-items { gap: var(--dcms-space-sm, 0.75rem); }
.dcms-accordion-boxed .dcms-accordion-item { border: var(--dcms-border-width, 1px) solid var(--dcms-color-border, #e2e8f0); border-radius: var(--dcms-radius, 0.625rem); padding: 0 var(--dcms-space-md, 1rem); background: var(--dcms-color-surface, #fff); }
.dcms-tabs-bar { display: flex; flex-wrap: wrap; gap: var(--dcms-space-xs, 0.5rem); margin-bottom: var(--dcms-space-md, 1rem); }
.dcms-tabs-bar button { border: 0; background: none; padding: 0.5rem 0.9rem; font: inherit; color: var(--dcms-color-muted, #64748b); cursor: pointer; border-radius: var(--dcms-radius-sm, 0.375rem); }
.dcms-tabs-bar button[aria-selected="true"] { color: var(--dcms-color-heading, #0f172a); }
.dcms-tabs-underline .dcms-tabs-bar { border-bottom: var(--dcms-border-width, 1px) solid var(--dcms-color-border, #e2e8f0); gap: 0; }
.dcms-tabs-underline .dcms-tabs-bar button { border-radius: 0; margin-bottom: -1px; border-bottom: 2px solid transparent; }
.dcms-tabs-underline .dcms-tabs-bar button[aria-selected="true"] { border-bottom-color: var(--dcms-color-brand, #4f46e5); }
.dcms-tabs-pills .dcms-tabs-bar button[aria-selected="true"] { background: var(--dcms-color-brand, #4f46e5); color: var(--dcms-color-brand-contrast, #fff); }
.dcms-tabs-panels { display: flex; flex-direction: column; gap: var(--dcms-space-lg, 2rem); }
.dcms-tab-label { font: 600 0.75rem system-ui, sans-serif; text-transform: uppercase; letter-spacing: 0.04em; color: var(--dcms-color-muted, #64748b); margin-bottom: 0.5rem; }
.dcms-tab-body { display: flex; flex-direction: column; gap: var(--dcms-space-sm, 0.75rem); }
.dcms-carousel { position: relative; }
.dcms-carousel-track { display: grid; grid-auto-flow: column; grid-auto-columns: 100%; gap: var(--dcms-space-md, 1rem); overflow-x: auto; scroll-snap-type: x mandatory; scrollbar-width: none; overscroll-behavior-x: contain; }
.dcms-carousel-track::-webkit-scrollbar { display: none; }
.dcms-carousel-track > * { scroll-snap-align: start; }
.dcms-carousel-2 .dcms-carousel-track { grid-auto-columns: calc((100% - var(--dcms-space-md, 1rem)) / 2); }
.dcms-carousel-3 .dcms-carousel-track { grid-auto-columns: calc((100% - 2 * var(--dcms-space-md, 1rem)) / 3); }
@media (max-width: 640px) { .dcms-carousel-2 .dcms-carousel-track, .dcms-carousel-3 .dcms-carousel-track { grid-auto-columns: 85%; } }
.dcms-carousel-controls { display: flex; align-items: center; justify-content: center; gap: var(--dcms-space-md, 1rem); margin-top: var(--dcms-space-md, 1rem); }
.dcms-carousel-arrow { display: grid; place-items: center; width: 2.5rem; height: 2.5rem; border-radius: 999px; border: var(--dcms-border-width, 1px) solid var(--dcms-color-border, #e2e8f0); background: var(--dcms-color-surface, #fff); color: var(--dcms-color-text, inherit); cursor: pointer; }
.dcms-carousel-arrow svg { width: 1.1rem; height: 1.1rem; }
.dcms-carousel-dots { display: flex; gap: 0.4rem; }
.dcms-carousel-dots button { width: 0.6rem; height: 0.6rem; padding: 0; border: 0; border-radius: 999px; background: var(--dcms-color-border-strong, #cbd5e1); cursor: pointer; }
.dcms-carousel-dots button[aria-current="true"] { background: var(--dcms-color-brand, #4f46e5); width: 1.4rem; }
.dcms-countdown { display: flex; flex-wrap: wrap; gap: var(--dcms-space-md, 1rem); }
.dcms-countdown-part { display: flex; flex-direction: column; align-items: center; min-width: 4.5rem; padding: var(--dcms-space-sm, 0.75rem); border-radius: var(--dcms-radius, 0.625rem); background: var(--dcms-color-surface-alt, #f8fafc); }
.dcms-countdown-value { font-family: var(--dcms-font-heading, inherit); font-size: var(--dcms-text-3xl, 2.25rem); font-weight: var(--dcms-weight-heading, 700); color: var(--dcms-color-heading, inherit); font-variant-numeric: tabular-nums; line-height: 1.1; }
.dcms-countdown-label { font-size: var(--dcms-text-sm, 0.875rem); color: var(--dcms-color-muted, #64748b); }
.dcms-countdown-done { font-size: var(--dcms-text-xl, 1.5rem); font-weight: 600; margin: 0; }
.dcms-social { display: flex; flex-wrap: wrap; gap: var(--dcms-space-sm, 0.75rem); list-style: none; margin: 0; padding: 0; }
.dcms-social a, .dcms-social-link { display: inline-flex; align-items: center; gap: 0.4rem; color: var(--dcms-color-text, inherit); text-decoration: none; }
.dcms-social svg { width: 1.25rem; height: 1.25rem; }
.dcms-social-circles a, .dcms-social-circles .dcms-social-link { justify-content: center; width: 2.5rem; height: 2.5rem; border-radius: 999px; background: var(--dcms-color-brand-soft, #eef2ff); color: var(--dcms-color-brand, #4f46e5); }
.dcms-social-circles a:hover { background: var(--dcms-color-brand, #4f46e5); color: var(--dcms-color-brand-contrast, #fff); }
.dcms-social-labels a:hover span { text-decoration: underline; }

.dcms-problem {
  padding: 0.75rem; border: 1px dashed #dc2626; border-radius: 0.375rem;
  color: #b91c1c; background: #fef2f2; font: 0.875rem system-ui, sans-serif;
}
`;


function responsiveCss(): string {
  const block = (prefix: string) =>
    RESPONSIVE_RULES.map(([cls, decl]) => `.${prefix}${cls} { ${decl} }`).join('\n');
  return [
    block(''),
    // A split collapses to one column on phones unless the author chose otherwise for mobile.
    `@media (max-width: ${MOBILE_MAX_WIDTH}px) { .dcms-split { grid-template-columns: minmax(0, 1fr); } }`,
    `@media (max-width: ${TABLET_MAX_WIDTH}px) {\n${block('t-')}\n}`,
    `@media (max-width: ${MOBILE_MAX_WIDTH}px) {\n${block('m-')}\n}`,
  ].join('\n');
}

export const RUNTIME_CSS = STATIC_CSS + responsiveCss();
