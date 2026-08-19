import { themeVariables, type ThemeTokens } from '@dcms/gjs-schema';
import { BLOCKS_CSS } from './blocks-css';

/**
 * Design kits — a complete look as a bundle of theme tokens.
 *
 * The problem this solves: a builder that ships one neutral default makes every
 * site look like an unstyled draft until somebody writes CSS, and the person
 * using a website builder is usually the person who does not want to write CSS.
 *
 * A kit is deliberately *only* tokens. It sets no rules of its own, so applying
 * one rewrites `styles/theme.css` (generated, safe to overwrite) and never
 * touches `styles/global.css` (the author's, hand-editable). That is what makes
 * switching kits reversible and non-destructive: every rule in the block
 * stylesheet reads `var(--dcms-…)`, so changing the values restyles the whole
 * site — headings, shadows, spacing rhythm, button shape — with nothing to undo.
 *
 * The constraint that keeps them honest: **no web fonts.** A published Mode A
 * page links its own stylesheets and nothing else, and a kit that quietly added
 * a request to a font CDN would make every visitor's first paint depend on a
 * third party. The kits differ by stack, weight, scale, tracking and colour
 * instead, which is enough to make them read as genuinely different designs.
 */

export interface KitPreview {
  /** Swatches for the picker tile, in the order they should be shown. */
  swatches: string[];
  /** The heading stack, so the tile can render its own name in the kit's face. */
  headingFont: string;
  /** Corner radius for the tile's sample chip. */
  radius: string;
  /** Page background the tile sits on, so a dark kit reads as dark. */
  surface: string;
  /** Text colour on that surface. */
  text: string;
}

export interface DesignKit {
  id: string;
  name: string;
  /** One line, shown under the name in the picker. */
  description: string;
  theme: ThemeTokens;
  preview: KitPreview;
}

const MONO = "ui-monospace, SFMono-Regular, 'SF Mono', Menlo, Consolas, monospace";
const SYSTEM_SANS = "system-ui, -apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif";
const GROTESK = "'Helvetica Neue', Helvetica, Arial, system-ui, sans-serif";
const HUMANIST = "'Segoe UI', Candara, Optima, 'Trebuchet MS', system-ui, sans-serif";
const SERIF = "Georgia, 'Iowan Old Style', 'Times New Roman', Times, serif";
const PALATINO = "'Palatino Linotype', Palatino, Georgia, 'Book Antiqua', serif";

/**
 * The spacing rhythm every kit uses, scaled by its own `step`.
 *
 * Kits differ in how much air they leave, but not in the *ratio* between steps —
 * a section that is twice a card's padding should stay twice it in every kit, or
 * blocks composed from these values stop lining up when the kit changes.
 */
function spacing(step: number): Record<string, string> {
  const round = (n: number) => `${Math.round(n * 1000) / 1000}rem`;
  return {
    '2xs': round(0.25 * step),
    xs: round(0.5 * step),
    sm: round(0.75 * step),
    md: round(1 * step),
    lg: round(2 * step),
    xl: round(4 * step),
    '2xl': round(6 * step),
    '3xl': round(8 * step),
  };
}

/**
 * A modular type scale.
 *
 * `ratio` is what actually makes a kit feel loud or quiet: at 1.2 the headings
 * sit close to the body text and the page reads as calm, at 1.333 the same
 * markup shouts. Sizes are `clamp()`ed so a 4xl heading is not 3.5rem on a
 * phone — responsive typography without the author writing a media query.
 */
function typeScale(ratio: number, base = 1): Record<string, string> {
  const at = (steps: number) => base * ratio ** steps;
  const fixed = (n: number) => `${Math.round(n * 1000) / 1000}rem`;
  // Below `base` nothing needs to shrink further on small screens.
  const fluid = (steps: number) => {
    const max = at(steps);
    const min = Math.max(base, max * 0.62);
    return `clamp(${fixed(min)}, ${Math.round((max - min) * 2.5 * 100) / 100}vw + ${fixed(min)}, ${fixed(max)})`;
  };
  return {
    xs: fixed(at(-2)),
    sm: fixed(at(-1)),
    base: fixed(base),
    lg: fixed(at(1)),
    xl: fixed(at(2)),
    '2xl': fluid(3),
    '3xl': fluid(4),
    '4xl': fluid(5),
    '5xl': fluid(6),
  };
}

/** Shadows from one colour, so a kit's elevation matches its palette. */
function shadows(rgb: string, strength: number): Record<string, string> {
  const a = (n: number) => `rgb(${rgb} / ${Math.round(n * strength * 100) / 100}%)`;
  return {
    xs: `0 1px 2px ${a(5)}`,
    sm: `0 1px 3px ${a(6)}, 0 1px 2px -1px ${a(6)}`,
    md: `0 4px 12px -2px ${a(8)}, 0 2px 6px -2px ${a(5)}`,
    lg: `0 12px 28px -8px ${a(12)}, 0 4px 10px -4px ${a(6)}`,
    xl: `0 24px 56px -12px ${a(16)}, 0 8px 20px -8px ${a(8)}`,
  };
}

export const DESIGN_KITS: DesignKit[] = [
  {
    id: 'studio',
    name: 'Studio',
    description: 'Clean and modern. Soft shadows, generous spacing, a confident blue.',
    theme: {
      colors: {
        brand: '#4f46e5',
        'brand-strong': '#4338ca',
        'brand-soft': '#eef2ff',
        'brand-contrast': '#ffffff',
        accent: '#0ea5e9',
        'accent-contrast': '#ffffff',
        text: '#1e293b',
        heading: '#0f172a',
        muted: '#64748b',
        surface: '#ffffff',
        'surface-alt': '#f8fafc',
        'surface-sunken': '#f1f5f9',
        inverse: '#0f172a',
        'inverse-text': '#f8fafc',
        border: '#e2e8f0',
        'border-strong': '#cbd5e1',
        success: '#16a34a',
        warning: '#d97706',
        danger: '#dc2626',
      },
      fonts: { body: SYSTEM_SANS, heading: SYSTEM_SANS, mono: MONO },
      spacing: spacing(1),
      text: typeScale(1.25),
      shadows: shadows('15 23 42', 1),
      metrics: {
        container: '72rem',
        'container-narrow': '44rem',
        'radius-sm': '0.375rem',
        'radius-lg': '1rem',
        'radius-pill': '999px',
        'border-width': '1px',
        transition: '160ms cubic-bezier(0.4, 0, 0.2, 1)',
        'leading-body': '1.65',
        'leading-heading': '1.15',
        'weight-heading': '650',
        'tracking-heading': '-0.02em',
        'tracking-label': '0.04em',
        'transform-heading': 'none',
        'section-py': '5rem',
        overlay: 'rgb(15 23 42 / 55%)',
      },
      radius: '0.625rem',
      custom: {},
    },
    preview: {
      swatches: ['#4f46e5', '#0ea5e9', '#f8fafc', '#1e293b'],
      headingFont: SYSTEM_SANS,
      radius: '0.625rem',
      surface: '#ffffff',
      text: '#1e293b',
    },
  },

  {
    id: 'editorial',
    name: 'Editorial',
    description: 'A magazine. Serif headings, wide measure, hairline rules, no shadows.',
    theme: {
      colors: {
        brand: '#9f1239',
        'brand-strong': '#881337',
        'brand-soft': '#fff1f2',
        'brand-contrast': '#fffbf5',
        accent: '#0f766e',
        'accent-contrast': '#ffffff',
        text: '#2b2622',
        heading: '#17130f',
        muted: '#7a6f66',
        surface: '#fffdf8',
        'surface-alt': '#f7f2e9',
        'surface-sunken': '#efe8dc',
        inverse: '#17130f',
        'inverse-text': '#fffdf8',
        border: '#e0d6c7',
        'border-strong': '#17130f',
        success: '#15803d',
        warning: '#b45309',
        danger: '#b91c1c',
      },
      fonts: { body: PALATINO, heading: SERIF, mono: MONO },
      spacing: spacing(1.15),
      text: typeScale(1.333, 1.0625),
      // Print does not have drop shadows; depth here comes from rules and space.
      shadows: { xs: 'none', sm: 'none', md: 'none', lg: 'none', xl: 'none' },
      metrics: {
        container: '68rem',
        'container-narrow': '38rem',
        'radius-sm': '0',
        'radius-lg': '0',
        'radius-pill': '0',
        'border-width': '1px',
        transition: '200ms ease',
        'leading-body': '1.75',
        'leading-heading': '1.1',
        'weight-heading': '700',
        'tracking-heading': '-0.015em',
        'tracking-label': '0.14em',
        'transform-heading': 'none',
        'section-py': '5.5rem',
        overlay: 'rgb(23 19 15 / 60%)',
      },
      radius: '0',
      custom: {},
    },
    preview: {
      swatches: ['#9f1239', '#0f766e', '#f7f2e9', '#17130f'],
      headingFont: SERIF,
      radius: '0',
      surface: '#fffdf8',
      text: '#2b2622',
    },
  },

  {
    id: 'bold',
    name: 'Bold',
    description: 'Loud. Oversized type, hard offset shadows, black rules, uppercase labels.',
    theme: {
      colors: {
        brand: '#111111',
        'brand-strong': '#000000',
        'brand-soft': '#fef08a',
        'brand-contrast': '#fde047',
        accent: '#fde047',
        'accent-contrast': '#111111',
        text: '#111111',
        heading: '#000000',
        muted: '#555555',
        surface: '#fefce8',
        'surface-alt': '#fef9c3',
        'surface-sunken': '#fef08a',
        inverse: '#111111',
        'inverse-text': '#fde047',
        border: '#111111',
        'border-strong': '#000000',
        success: '#15803d',
        warning: '#c2410c',
        danger: '#b91c1c',
      },
      fonts: { body: GROTESK, heading: GROTESK, mono: MONO },
      spacing: spacing(1),
      text: typeScale(1.4),
      // Flat offset shadows, not blur: the look is print-block, not elevation.
      shadows: {
        xs: '2px 2px 0 #111111',
        sm: '3px 3px 0 #111111',
        md: '5px 5px 0 #111111',
        lg: '8px 8px 0 #111111',
        xl: '12px 12px 0 #111111',
      },
      metrics: {
        container: '76rem',
        'container-narrow': '46rem',
        'radius-sm': '0',
        'radius-lg': '0',
        'radius-pill': '0',
        'border-width': '2px',
        transition: '120ms steps(3, end)',
        'leading-body': '1.55',
        'leading-heading': '0.95',
        'weight-heading': '800',
        'tracking-heading': '-0.045em',
        'tracking-label': '0.18em',
        'transform-heading': 'uppercase',
        'section-py': '5rem',
        overlay: 'rgb(17 17 17 / 70%)',
      },
      radius: '0',
      custom: {},
    },
    preview: {
      swatches: ['#111111', '#fde047', '#fef9c3', '#000000'],
      headingFont: GROTESK,
      radius: '0',
      surface: '#fefce8',
      text: '#111111',
    },
  },

  {
    id: 'soft',
    name: 'Soft',
    description: 'Friendly and rounded. Pill buttons, pastel surfaces, diffuse colour shadows.',
    theme: {
      colors: {
        brand: '#7c3aed',
        'brand-strong': '#6d28d9',
        'brand-soft': '#f3e8ff',
        'brand-contrast': '#ffffff',
        accent: '#ec4899',
        'accent-contrast': '#ffffff',
        text: '#3b3350',
        heading: '#2a2340',
        muted: '#7c7391',
        surface: '#ffffff',
        'surface-alt': '#faf7ff',
        'surface-sunken': '#f3edff',
        inverse: '#2a2340',
        'inverse-text': '#faf7ff',
        border: '#ece5f8',
        'border-strong': '#d8cbf0',
        success: '#059669',
        warning: '#ea580c',
        danger: '#e11d48',
      },
      fonts: { body: HUMANIST, heading: HUMANIST, mono: MONO },
      spacing: spacing(1.1),
      text: typeScale(1.2),
      // Tinted rather than neutral, so elevation feels part of the palette.
      shadows: shadows('124 58 237', 1.6),
      metrics: {
        container: '70rem',
        'container-narrow': '42rem',
        'radius-sm': '0.75rem',
        'radius-lg': '2rem',
        'radius-pill': '999px',
        'border-width': '1px',
        transition: '220ms cubic-bezier(0.34, 1.56, 0.64, 1)',
        'leading-body': '1.7',
        'leading-heading': '1.2',
        'weight-heading': '600',
        'tracking-heading': '-0.01em',
        'tracking-label': '0.02em',
        'transform-heading': 'none',
        'section-py': '5rem',
        overlay: 'rgb(42 35 64 / 50%)',
      },
      radius: '1.25rem',
      custom: {},
    },
    preview: {
      swatches: ['#7c3aed', '#ec4899', '#f3edff', '#2a2340'],
      headingFont: HUMANIST,
      radius: '1.25rem',
      surface: '#ffffff',
      text: '#3b3350',
    },
  },

  {
    id: 'noir',
    name: 'Noir',
    description: 'Dark by default. Deep surfaces, thin borders, a single bright accent.',
    theme: {
      colors: {
        brand: '#22d3ee',
        'brand-strong': '#67e8f9',
        'brand-soft': '#0e3a45',
        'brand-contrast': '#04141a',
        accent: '#a78bfa',
        'accent-contrast': '#0b0f14',
        text: '#cbd5e1',
        heading: '#f1f5f9',
        muted: '#8194ab',
        surface: '#0b0f14',
        'surface-alt': '#121820',
        'surface-sunken': '#182029',
        inverse: '#f1f5f9',
        'inverse-text': '#0b0f14',
        border: '#22303d',
        'border-strong': '#324656',
        success: '#34d399',
        warning: '#fbbf24',
        danger: '#fb7185',
      },
      fonts: { body: SYSTEM_SANS, heading: GROTESK, mono: MONO },
      spacing: spacing(1),
      text: typeScale(1.28),
      // On a dark ground a black shadow is invisible; depth comes from a glow.
      shadows: {
        xs: '0 0 0 1px rgb(34 211 238 / 6%)',
        sm: '0 1px 3px rgb(0 0 0 / 60%), 0 0 0 1px rgb(34 211 238 / 6%)',
        md: '0 6px 20px -6px rgb(0 0 0 / 70%), 0 0 0 1px rgb(34 211 238 / 8%)',
        lg: '0 16px 40px -12px rgb(0 0 0 / 80%), 0 0 24px -8px rgb(34 211 238 / 12%)',
        xl: '0 28px 64px -16px rgb(0 0 0 / 85%), 0 0 40px -12px rgb(34 211 238 / 16%)',
      },
      metrics: {
        container: '74rem',
        'container-narrow': '44rem',
        'radius-sm': '0.25rem',
        'radius-lg': '0.75rem',
        'radius-pill': '999px',
        'border-width': '1px',
        transition: '160ms ease',
        'leading-body': '1.7',
        'leading-heading': '1.1',
        'weight-heading': '600',
        'tracking-heading': '-0.025em',
        'tracking-label': '0.12em',
        'transform-heading': 'none',
        'section-py': '5.5rem',
        overlay: 'rgb(4 8 12 / 72%)',
      },
      radius: '0.5rem',
      custom: {},
    },
    preview: {
      swatches: ['#22d3ee', '#a78bfa', '#121820', '#f1f5f9'],
      headingFont: GROTESK,
      radius: '0.5rem',
      surface: '#0b0f14',
      text: '#cbd5e1',
    },
  },

  {
    id: 'terra',
    name: 'Terra',
    description: 'Warm and grounded. Earth tones, humanist type, natural spacing.',
    theme: {
      colors: {
        brand: '#9a6a3f',
        'brand-strong': '#7c5230',
        'brand-soft': '#f6ecdf',
        'brand-contrast': '#fffaf3',
        accent: '#4f7a5c',
        'accent-contrast': '#ffffff',
        text: '#3b332b',
        heading: '#2a231d',
        muted: '#857a6c',
        surface: '#fffaf3',
        'surface-alt': '#f6efe4',
        'surface-sunken': '#ece2d3',
        inverse: '#2a231d',
        'inverse-text': '#fffaf3',
        border: '#e2d6c4',
        'border-strong': '#c9b79e',
        success: '#4f7a5c',
        warning: '#b06f2b',
        danger: '#a63d31',
      },
      fonts: { body: HUMANIST, heading: SERIF, mono: MONO },
      spacing: spacing(1.05),
      text: typeScale(1.26, 1.0625),
      shadows: shadows('61 46 32', 1.2),
      metrics: {
        container: '70rem',
        'container-narrow': '40rem',
        'radius-sm': '0.25rem',
        'radius-lg': '0.75rem',
        'radius-pill': '999px',
        'border-width': '1px',
        transition: '180ms ease',
        'leading-body': '1.72',
        'leading-heading': '1.18',
        'weight-heading': '600',
        'tracking-heading': '-0.01em',
        'tracking-label': '0.1em',
        'transform-heading': 'none',
        'section-py': '5rem',
        overlay: 'rgb(42 35 29 / 55%)',
      },
      radius: '0.5rem',
      custom: {},
    },
    preview: {
      swatches: ['#9a6a3f', '#4f7a5c', '#f6efe4', '#2a231d'],
      headingFont: SERIF,
      radius: '0.5rem',
      surface: '#fffaf3',
      text: '#3b332b',
    },
  },
];

/** The kit a brand-new site starts on. */
export const DEFAULT_KIT_ID = 'studio';

export function findKit(id: string | undefined): DesignKit | undefined {
  return DESIGN_KITS.find((kit) => kit.id === id);
}

export function defaultKit(): DesignKit {
  // Non-null: DEFAULT_KIT_ID names a kit in the array above, and kitCatalogue's
  // test asserts it.
  return findKit(DEFAULT_KIT_ID)!;
}

/**
 * Apply a kit to an existing theme.
 *
 * The kit replaces every group it defines, because a half-applied kit — new
 * colours over the old type scale — looks worse than either. `custom` is the
 * exception and is carried over: those are the author's own variables, which no
 * kit knows about and none should silently delete.
 */
export function applyKit(theme: ThemeTokens, kit: DesignKit): ThemeTokens {
  return { ...kit.theme, custom: { ...theme.custom } };
}

/**
 * Which kit a theme currently matches, if any.
 *
 * Compared on the values that carry the look rather than deep-equality, so a
 * theme still counts as "Studio" after the author nudges one spacing step — the
 * picker should show what they are on, not drop to "Custom" at the first edit.
 */
export function matchKit(theme: ThemeTokens): DesignKit | undefined {
  return DESIGN_KITS.find(
    (kit) =>
      kit.theme.colors.brand === theme.colors?.brand &&
      kit.theme.colors.surface === theme.colors?.surface &&
      kit.theme.fonts.heading === theme.fonts?.heading,
  );
}

/** Every `var(--dcms-…)` the block stylesheet reads. */
function tokensUsedByBlocks(): string[] {
  const used = new Set<string>();
  for (const match of BLOCKS_CSS.matchAll(/var\(\s*(--dcms-[a-z0-9-]+)/g)) used.add(match[1]!);
  return [...used].sort();
}

/**
 * Tokens the block stylesheet reads that this theme does not define.
 *
 * This is the check that has to happen before `styles/global.css` is refreshed
 * on an existing site. An undefined custom property does not error — the
 * declaration is simply dropped — so a site whose theme predates the current
 * stylesheet would come back with no type scale, no radii and no shadows, and
 * nothing anywhere would say why. A site seeded before design kits existed is
 * missing three quarters of them, which is not a degradation, it is a broken
 * page.
 *
 * The caller's job is to apply a kit in the same edit, not to warn and proceed.
 */
export function missingThemeTokens(theme: ThemeTokens): string[] {
  const defined = new Set(themeVariables(theme).map((v) => v.name));
  return tokensUsedByBlocks().filter((token) => !defined.has(token));
}

/** Can this theme carry the current block stylesheet on its own? */
export function themeCoversBlocks(theme: ThemeTokens): boolean {
  return missingThemeTokens(theme).length === 0;
}
