import { z } from 'zod';

/**
 * The design vocabulary a site is styled with — shared by Mode A (`site.json.theme`, rendered
 * to `styles/theme.css`) and Mode D (`dcms/theme.json`), so a design kit applies to either.
 *
 * It lives here rather than in `@dcms/gjs-schema` because this package is copied verbatim into
 * every Mode D site and may import nothing but itself; `@dcms/gjs-schema` re-exports it. This
 * module must stay React-free: Mode A reaches it through the `@dcms/site-runtime/theme` subpath.
 *
 * The groups are deliberately wider than colours and fonts. A look is not just a
 * palette — it is also how hard the shadows are, how tight the headings track,
 * how fast the type scale grows and how much air a section gets. Keeping all of
 * that as tokens is what lets a design kit be a plain data bundle: applying one
 * restyles the whole site without the author writing a line of CSS.
 *
 * The rule for what belongs here: if a stylesheet reads it as `var(--dcms-…)`, it
 * is a token. Anything else is `custom`.
 */
export const themeTokensSchema = z.object({
  /** Token name → CSS colour, emitted as `--dcms-color-<name>`. */
  colors: z.record(z.string(), z.string()).default({}),
  /** Token name → font stack, emitted as `--dcms-font-<name>`. */
  fonts: z.record(z.string(), z.string()).default({}),
  /** Token name → length, emitted as `--dcms-space-<name>`. */
  spacing: z.record(z.string(), z.string()).default({}),
  /** Step name → font size, emitted as `--dcms-text-<name>`. The type scale. */
  text: z.record(z.string(), z.string()).default({}),
  /** Step name → box-shadow, emitted as `--dcms-shadow-<name>`. */
  shadows: z.record(z.string(), z.string()).default({}),
  /**
   * The remaining named design decisions, emitted as `--dcms-<name>` with no
   * group infix: `container`, `radius-sm`, `radius-lg`, `radius-pill`,
   * `border-width`, `transition`, `leading-body`, `leading-heading`,
   * `weight-heading`, `tracking-heading`, `transform-heading`, `section-py`.
   *
   * One flat group rather than six single-purpose ones, because these share
   * nothing but being scalar — and a group per token would be a schema change
   * every time a kit wants one more knob.
   */
  metrics: z.record(z.string(), z.string()).default({}),
  /** Default corner radius, emitted as `--dcms-radius`. */
  radius: z.string().optional(),
  /** Escape hatch: raw custom properties merged into `:root` verbatim. */
  custom: z.record(z.string(), z.string()).default({}),
});

export type ThemeTokens = z.infer<typeof themeTokensSchema>;

/** A theme that defines nothing — every group present and empty. */
export function emptyTheme(): ThemeTokens {
  return { colors: {}, fonts: {}, spacing: {}, text: {}, shadows: {}, metrics: {}, custom: {} };
}

/** The prefixed token groups. `metrics` is unprefixed and handled separately. */
export type TokenGroup = 'color' | 'font' | 'space' | 'text' | 'shadow';

/** `--dcms-color-brand`, `--dcms-font-body`, `--dcms-space-lg`, `--dcms-text-xl`. */
export function themeVarName(group: TokenGroup, token: string): string {
  return `--dcms-${group}-${cssIdent(token)}`;
}

/** Every variable this theme defines, for the code view's completion list. */
export function themeVariables(theme: ThemeTokens): { name: string; value: string }[] {
  const vars: { name: string; value: string }[] = [];
  const group = (entries: Record<string, string> | undefined, name: TokenGroup) => {
    for (const [token, value] of Object.entries(entries ?? {})) {
      vars.push({ name: themeVarName(name, token), value });
    }
  };

  group(theme.colors, 'color');
  group(theme.fonts, 'font');
  group(theme.spacing, 'space');
  group(theme.text, 'text');
  group(theme.shadows, 'shadow');
  // Metrics carry no group infix — the token name is already the whole name
  // (`container`, `radius-lg`, `tracking-heading`), so prefixing would give
  // `--dcms-metric-radius-lg`, which reads worse in a stylesheet than the thing
  // it describes.
  for (const [token, value] of Object.entries(theme.metrics ?? {})) {
    vars.push({ name: `--dcms-${cssIdent(token)}`, value });
  }
  if (theme.radius) vars.push({ name: '--dcms-radius', value: theme.radius });
  for (const [name, value] of Object.entries(theme.custom ?? {})) {
    vars.push({ name: name.startsWith('--') ? name : `--${cssIdent(name)}`, value });
  }
  return vars;
}

/** The theme as one `:root { … }` block. */
export function themeRootCss(theme: ThemeTokens): string {
  const vars = themeVariables(theme);
  if (vars.length === 0) return ':root {\n}\n';
  const body = vars.map((v) => `  ${v.name}: ${cssValue(v.value)};`).join('\n');
  return `:root {\n${body}\n}\n`;
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
