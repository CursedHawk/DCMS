import type { DesignKit } from '@dcms/gjs-blocks';
import type { ThemeTokens } from '@dcms/site-runtime';

/** The adjustments the theme panel offers on top of a design kit (Mode D v2, U3.5). */
export type Tuning = NonNullable<ThemeTokens['tuning']>;

/** Fonts every visitor already has: no web font to load, nothing to license. */
export const FONTS: readonly { id: string; label: string; stack: string }[] = [
  { id: 'system', label: 'System', stack: "system-ui, -apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif" },
  { id: 'grotesk', label: 'Grotesk', stack: "'Helvetica Neue', Helvetica, Arial, system-ui, sans-serif" },
  { id: 'humanist', label: 'Humanist', stack: "'Segoe UI', Candara, Optima, 'Trebuchet MS', system-ui, sans-serif" },
  { id: 'rounded', label: 'Rounded', stack: "ui-rounded, 'SF Pro Rounded', 'Arial Rounded MT Bold', system-ui, sans-serif" },
  { id: 'serif', label: 'Serif', stack: "Georgia, 'Iowan Old Style', 'Times New Roman', Times, serif" },
  { id: 'book', label: 'Book', stack: "'Palatino Linotype', Palatino, Georgia, 'Book Antiqua', serif" },
  { id: 'slab', label: 'Slab', stack: "Rockwell, 'Roboto Slab', 'Courier New', serif" },
];

export const ROUNDNESS = [0, 0.5, 1, 1.8] as const;
export const DENSITY = [0.8, 1, 1.25] as const;
export const SHADOW = [0, 0.5, 1, 1.7] as const;

// --- Colour -----------------------------------------------------------------------------

type Rgb = [number, number, number];

function parseHex(hex: string): Rgb | null {
  const m = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return null;
  const h = m[1]!.length === 3 ? [...m[1]!].map((c) => c + c).join('') : m[1]!;
  return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16)) as Rgb;
}

const toHex = (rgb: Rgb) => `#${rgb.map((v) => Math.round(Math.max(0, Math.min(255, v))).toString(16).padStart(2, '0')).join('')}`;
const mix = (a: Rgb, b: Rgb, t: number): Rgb => a.map((v, i) => v + (b[i]! - v) * t) as Rgb;

function luminance([r, g, b]: Rgb): number {
  const c = (v: number) => {
    const s = v / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * c(r) + 0.7152 * c(g) + 0.0722 * c(b);
}

/** WCAG contrast ratio of two hex colours (1–21); 1 when either does not parse. */
export function contrast(a: string, b: string): number {
  const x = parseHex(a);
  const y = parseHex(b);
  if (!x || !y) return 1;
  const [hi, lo] = [luminance(x), luminance(y)].sort((p, q) => q - p);
  return (hi! + 0.05) / (lo! + 0.05);
}

/** A colour's family: itself, a darker one for hover, a pale tint, and the readable text on it. */
function family(hex: string): { base: string; strong: string; soft: string; contrast: string } | null {
  const rgb = parseHex(hex);
  if (!rgb) return null;
  const text = contrast(hex, '#ffffff') >= contrast(hex, '#111827') ? '#ffffff' : '#111827';
  return { base: toHex(rgb), strong: toHex(mix(rgb, [0, 0, 0], 0.15)), soft: toHex(mix(rgb, [255, 255, 255], 0.9)), contrast: text };
}

// --- Lengths ----------------------------------------------------------------------------

/** Every length in a value, multiplied: "1rem 2.5rem" × 1.2 → "1.2rem 3rem". */
function scaleLengths(value: string, by: number): string {
  return value.replace(/(-?\d*\.?\d+)(rem|em|px)\b/g, (_, n: string, unit: string) => `${+(parseFloat(n) * by).toFixed(3)}${unit}`);
}

/** Every alpha in a shadow, multiplied: how strong it is, not where it falls. */
function scaleAlpha(value: string, by: number): string {
  if (by === 0) return 'none';
  return value.replace(/\/\s*(\d*\.?\d+)(%?)\s*\)/g, (_, n: string, pct: string) => {
    const max = pct ? 100 : 1;
    return `/ ${+Math.min(max, parseFloat(n) * by).toFixed(3)}${pct})`;
  });
}

const mapValues = (o: Record<string, string>, f: (v: string) => string) => Object.fromEntries(Object.entries(o).map(([k, v]) => [k, f(v)]));

/**
 * The kit's tokens with the tuning applied — always from the kit, never on top of an earlier
 * tuning, so changing a setting back gives exactly the kit again.
 */
export function tuneTheme(kit: DesignKit, tuning: Tuning, custom: Record<string, string> = {}): ThemeTokens {
  const base = kit.theme;
  const colors = { ...base.colors };
  const brand = tuning.brand ? family(tuning.brand) : null;
  if (brand) Object.assign(colors, { brand: brand.base, 'brand-strong': brand.strong, 'brand-soft': brand.soft, 'brand-contrast': brand.contrast });
  const accent = tuning.accent ? family(tuning.accent) : null;
  if (accent) Object.assign(colors, { accent: accent.base, 'accent-contrast': accent.contrast });

  const font = (id: string | undefined) => FONTS.find((f) => f.id === id)?.stack;
  const fonts = { ...base.fonts, ...(font(tuning.headingFont) ? { heading: font(tuning.headingFont)! } : {}), ...(font(tuning.bodyFont) ? { body: font(tuning.bodyFont)! } : {}) };

  const round = tuning.roundness ?? 1;
  const dense = tuning.density ?? 1;
  const metrics = { ...base.metrics };
  for (const key of ['radius-sm', 'radius-lg'] as const) if (metrics[key]) metrics[key] = scaleLengths(metrics[key]!, round);
  if (metrics['section-py']) metrics['section-py'] = scaleLengths(metrics['section-py'], dense);

  const tuned = Object.values(tuning).some((v) => v !== undefined);
  return {
    ...base,
    colors,
    fonts,
    spacing: dense === 1 ? base.spacing : mapValues(base.spacing, (v) => scaleLengths(v, dense)),
    shadows: (tuning.shadow ?? 1) === 1 ? base.shadows : mapValues(base.shadows, (v) => scaleAlpha(v, tuning.shadow!)),
    metrics,
    radius: base.radius && round !== 1 ? scaleLengths(base.radius, round) : base.radius,
    custom: { ...custom },
    kit: kit.id,
    ...(tuned ? { tuning } : {}),
  };
}
