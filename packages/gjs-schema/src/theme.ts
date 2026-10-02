import { themeRootCss, type ThemeTokens } from '@dcms/site-runtime/theme';

/**
 * `styles/theme.css` is generated from `site.json.theme` on every save. Keeping
 * it as a real file rather than an inline `<style>` means the canvas iframe, the
 * Monaco CSS worker and the published page all resolve `var(--dcms-*)` the same
 * way, and a theme change shows up as a readable diff.
 *
 * The token vocabulary and the variable naming are shared with Mode D and live in
 * `@dcms/site-runtime/theme`; only the generated file's header is Mode A's own.
 */

export { themeVarName, themeVariables, type TokenGroup } from '@dcms/site-runtime/theme';

export const THEME_HEADER =
  '/* Generated from site.json — edit the theme in the builder, not this file. */';

export function renderThemeCss(theme: ThemeTokens): string {
  return `${THEME_HEADER}\n${themeRootCss(theme)}`;
}
