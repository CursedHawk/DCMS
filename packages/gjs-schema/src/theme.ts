import type { ThemeTokens } from './site';

/**
 * `styles/theme.css` is generated from `site.json.theme` on every save. Keeping
 * it as a real file rather than an inline `<style>` means the canvas iframe, the
 * Monaco CSS worker and the published page all resolve `var(--dcms-*)` the same
 * way, and a theme change shows up as a readable diff.
 */

export const THEME_HEADER =
  '/* Generated from site.json — edit the theme in the builder, not this file. */';

/** `--dcms-color-brand`, `--dcms-font-body`, `--dcms-space-lg`, `--dcms-radius`. */
export function themeVarName(group: 'color' | 'font' | 'space', token: string): string {
  return `--dcms-${group}-${cssIdent(token)}`;
}

/** Every variable this theme defines, for the code view's completion list. */
export function themeVariables(theme: ThemeTokens): { name: string; value: string }[] {
  const vars: { name: string; value: string }[] = [];
  for (const [token, value] of Object.entries(theme.colors ?? {})) {
    vars.push({ name: themeVarName('color', token), value });
  }
  for (const [token, value] of Object.entries(theme.fonts ?? {})) {
    vars.push({ name: themeVarName('font', token), value });
  }
  for (const [token, value] of Object.entries(theme.spacing ?? {})) {
    vars.push({ name: themeVarName('space', token), value });
  }
  if (theme.radius) vars.push({ name: '--dcms-radius', value: theme.radius });
  for (const [name, value] of Object.entries(theme.custom ?? {})) {
    vars.push({ name: name.startsWith('--') ? name : `--${cssIdent(name)}`, value });
  }
  return vars;
}

export function renderThemeCss(theme: ThemeTokens): string {
  const vars = themeVariables(theme);
  if (vars.length === 0) return `${THEME_HEADER}\n:root {\n}\n`;
  const body = vars.map((v) => `  ${v.name}: ${cssValue(v.value)};`).join('\n');
  return `${THEME_HEADER}\n:root {\n${body}\n}\n`;
}

/** Token names come from user input; keep them to a safe custom-property ident. */
function cssIdent(token: string): string {
  return token.trim().replace(/[^a-zA-Z0-9_-]+/g, '-').replace(/^-+|-+$/g, '') || 'token';
}

/**
 * A declaration value cannot contain `;`, `}` or a comment opener without
 * escaping the stylesheet. Strip rather than escape: a token value that needs
 * those characters is a mistake, and silently truncating is safer than emitting
 * CSS that changes the meaning of the rules after it.
 */
function cssValue(value: string): string {
  return value
    .replace(/[;{}]/g, ' ')
    .replace(/\/\*/g, ' ')
    .replace(/\*\//g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}
