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
