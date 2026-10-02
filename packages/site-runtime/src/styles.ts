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
export const RUNTIME_CSS = `
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
.dcms-width-narrow { max-width: var(--dcms-container-narrow, 44rem); }
.dcms-width-normal { max-width: var(--dcms-container, 72rem); }
.dcms-width-wide { max-width: calc(var(--dcms-container, 72rem) * 1.25); }
.dcms-width-full { max-width: none; }

.dcms-section { display: block; }
.dcms-py-none { padding-block: 0; }
.dcms-py-sm { padding-block: calc(var(--dcms-section-py, 5rem) * 0.4); }
.dcms-py-md { padding-block: var(--dcms-section-py, 5rem); }
.dcms-py-lg { padding-block: calc(var(--dcms-section-py, 5rem) * 1.6); }
.dcms-bg-alt { background: var(--dcms-color-surface-alt, #f8fafc); }
.dcms-bg-soft { background: var(--dcms-color-brand-soft, #eef2ff); }
.dcms-bg-inverse { background: var(--dcms-color-inverse, #0f172a); color: var(--dcms-color-inverse-text, #f8fafc); }
.dcms-bg-inverse .dcms-heading, .dcms-bg-inverse .dcms-text { color: inherit; }

.dcms-stack { display: flex; }
.dcms-stack-vertical { flex-direction: column; }
.dcms-stack-horizontal { flex-direction: row; }
.dcms-wrap { flex-wrap: wrap; }
.dcms-gap-none { gap: 0; }
.dcms-gap-xs { gap: var(--dcms-space-xs, 0.5rem); }
.dcms-gap-sm { gap: var(--dcms-space-sm, 0.75rem); }
.dcms-gap-md { gap: var(--dcms-space-md, 1rem); }
.dcms-gap-lg { gap: var(--dcms-space-lg, 2rem); }
.dcms-gap-xl { gap: var(--dcms-space-xl, 4rem); }
.dcms-align-start { align-items: flex-start; }
.dcms-align-center { align-items: center; }
.dcms-align-end { align-items: flex-end; }
.dcms-align-stretch { align-items: stretch; }
.dcms-justify-start { justify-content: flex-start; }
.dcms-justify-center { justify-content: center; }
.dcms-justify-end { justify-content: flex-end; }
.dcms-justify-between { justify-content: space-between; }

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
.dcms-size-sm { font-size: var(--dcms-text-sm, 0.875rem); }
.dcms-size-base { font-size: var(--dcms-text-base, 1rem); }
.dcms-size-lg { font-size: var(--dcms-text-lg, 1.25rem); }
.dcms-tone-muted { color: var(--dcms-color-muted, #64748b); }
.dcms-text-start { text-align: start; }
.dcms-text-center { text-align: center; }
.dcms-text-end { text-align: end; }

.dcms-image { display: block; width: 100%; height: auto; }
.dcms-ratio-16-9 { aspect-ratio: 16 / 9; }
.dcms-ratio-4-3 { aspect-ratio: 4 / 3; }
.dcms-ratio-1-1 { aspect-ratio: 1 / 1; }
.dcms-ratio-3-4 { aspect-ratio: 3 / 4; }
.dcms-ratio-16-9, .dcms-ratio-4-3, .dcms-ratio-1-1, .dcms-ratio-3-4 { height: auto; }
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

.dcms-problem {
  padding: 0.75rem; border: 1px dashed #dc2626; border-radius: 0.375rem;
  color: #b91c1c; background: #fef2f2; font: 0.875rem system-ui, sans-serif;
}
`;
