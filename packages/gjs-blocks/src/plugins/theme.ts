import { themeVariables, type ThemeTokens } from '@dcms/gjs-schema';
import type { Editor } from 'grapesjs';

/**
 * Style Manager configuration, bound to the site's theme tokens.
 *
 * The colour properties are seeded with the theme's palette so restyling a
 * component reaches for `var(--dcms-color-brand)` rather than a hex code an
 * author picked once and can never change globally. That is the difference
 * between a site with a theme and a site with a lot of blue in it.
 *
 * The sectors are also what the admin's own Styles panel renders — it runs in
 * `custom: true` mode, so GrapesJS holds this structure and our React component
 * draws it in the admin design system.
 */

/**
 * There is deliberately no `dcmsTheme` *plugin*.
 *
 * A GrapesJS plugin runs inside `grapesjs.init`, before the Style Manager builds
 * its sectors (it does that in its own `onLoad`, later in the same init). A
 * plugin therefore cannot see the sectors it needs: the first version of this
 * module applied the swatches inline, found nothing, logged
 * "'typography' sector not found" and left every colour picker without a single
 * theme preset — with no other symptom.
 *
 * Deferring to `editor.onReady` does not fix it either: `ready` additionally
 * waits on the canvas frame, which never loads in a headless editor, so the
 * callback would silently never run for tests and any non-browser consumer.
 *
 * The sectors do exist the moment `grapesjs.init` *returns*, so the caller
 * applies the swatches there — see `createEditor`. That is one line at the call
 * site and no ordering assumption at all.
 */

/**
 * Offer the theme's colours as Style Manager options. Called again whenever the
 * theme changes so the palette in the panel never goes stale.
 */
export function applyThemeSwatches(editor: Editor, theme?: ThemeTokens): void {
  if (!theme) return;
  const colors = themeVariables(theme)
    .filter((v) => v.name.startsWith('--dcms-color-'))
    .map((v) => ({ id: `var(${v.name})`, label: prettify(v.name), value: v.value }));
  if (!colors.length) return;

  for (const property of ['color', 'background-color', 'border-color']) {
    // Sectors are looked up by *id*, not by display name — passing 'Typography'
    // matched nothing, logged "'Typography' sector not found" on every call and
    // left the palette without a single theme swatch.
    const model = editor.StyleManager.getProperty('typography', property)
      ?? editor.StyleManager.getProperty('decorations', property);
    // `options` is what the colour trait offers as presets; the free-form picker
    // stays available, so this widens the choice rather than restricting it.
    model?.set('options' as never, colors as never);
  }
}

function prettify(varName: string): string {
  const token = varName.replace('--dcms-color-', '').replace(/-/g, ' ');
  return token.charAt(0).toUpperCase() + token.slice(1);
}

/**
 * The sector layout the Styles panel renders.
 *
 * Declared here rather than in the editor config so the panel and any headless
 * consumer (tests, the AI prompt) agree on what is styleable.
 */
export const STYLE_SECTORS = [
  {
    id: 'layout',
    name: 'Layout',
    open: true,
    properties: ['display', 'flex-direction', 'justify-content', 'align-items', 'gap', 'grid-template-columns'],
  },
  {
    id: 'spacing',
    name: 'Spacing',
    open: true,
    properties: ['padding', 'margin'],
  },
  {
    id: 'size',
    name: 'Size',
    open: false,
    properties: ['width', 'height', 'max-width', 'min-height'],
  },
  {
    id: 'typography',
    name: 'Typography',
    open: false,
    properties: ['font-family', 'font-size', 'font-weight', 'line-height', 'letter-spacing', 'text-align', 'color'],
  },
  {
    id: 'decorations',
    name: 'Decoration',
    open: false,
    properties: ['background-color', 'background-image', 'border-radius', 'border', 'box-shadow', 'opacity'],
  },
  {
    id: 'effects',
    name: 'Effects',
    open: false,
    properties: ['transition', 'transform', 'overflow', 'cursor'],
  },
] as const;
