import type { DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Wireframe thumbnails for the block palette.
 *
 * A 22px glyph tells an author that a block exists; it does not tell them what
 * dropping it will do. Three of the section icons are four rounded rectangles,
 * and picking between "Feature grid", "Card grid" and "Team" from those is
 * guessing. A wireframe shows the shape of the thing — where the image sits, how
 * many columns, whether the text is centred — which is the only question the
 * palette is actually being asked.
 *
 * They are drawn rather than derived. A generic derivation from the snippet's
 * DOM produces a mush of grey boxes that is technically accurate and visually
 * useless; a small set of hand-drawn archetypes, chosen per spec, produces
 * something legible at 96×60. The archetype is picked from the spec — its type,
 * then its icon, then its category — so a generated plugin block gets one too,
 * from the layout its content type implies.
 *
 * Two tones and no colour of their own: structure is `currentColor` at low
 * opacity, and anything the eye should land on first carries `.dcms-thumb-accent`
 * for the host to paint. That way the palette follows the admin theme, in light
 * and dark, without this file knowing either.
 */

const W = 96;
const H = 60;

interface Box {
  x: number;
  y: number;
  w: number;
  h: number;
  /** Corner radius. */
  r?: number;
  /** Fill opacity, on the structural tone. */
  o?: number;
  /** Paint with the accent colour instead. */
  accent?: boolean;
}

function rect({ x, y, w, h, r = 1.5, o = 0.16, accent = false }: Box): string {
  const fill = accent ? 'class="dcms-thumb-accent"' : `opacity="${o}"`;
  return `<rect x="${round(x)}" y="${round(y)}" width="${round(w)}" height="${round(h)}" rx="${r}" ${fill}/>`;
}

function circle(cx: number, cy: number, r: number, accent = false, o = 0.16): string {
  const fill = accent ? 'class="dcms-thumb-accent"' : `opacity="${o}"`;
  return `<circle cx="${round(cx)}" cy="${round(cy)}" r="${round(r)}" ${fill}/>`;
}

function round(value: number): number {
  return Math.round(value * 10) / 10;
}

/** A run of text lines, the last one short — the shape reading matter makes. */
function lines(x: number, y: number, w: number, count: number, gap = 4, o = 0.22): string {
  const out: string[] = [];
  for (let i = 0; i < count; i++) {
    out.push(rect({ x, y: y + i * gap, w: i === count - 1 ? w * 0.62 : w, h: 2, r: 1, o }));
  }
  return out.join('');
}

/** A heading bar: shorter, thicker and darker than body text. */
function heading(x: number, y: number, w: number, h = 4, o = 0.4): string {
  return rect({ x, y, w, h, r: 1, o });
}

function button(x: number, y: number, w = 18, h = 6): string {
  return rect({ x, y, w, h, r: 3, accent: true });
}

/** `count` evenly spaced columns across the content width, as x/width pairs. */
function columns(count: number, x = 8, width = W - 16, gap = 4): { x: number; w: number }[] {
  const w = (width - gap * (count - 1)) / count;
  return Array.from({ length: count }, (_, i) => ({ x: x + i * (w + gap), w }));
}

/** A card: image band on top, two text lines under it. */
function card(x: number, y: number, w: number, h: number, withImage = true): string {
  const imageH = withImage ? h * 0.45 : 0;
  return (
    rect({ x, y, w, h, r: 2, o: 0.1 }) +
    (withImage ? rect({ x, y, w, h: imageH, r: 2, o: 0.2 }) : '') +
    heading(x + 4, y + imageH + 5, Math.min(w - 8, w * 0.7), 3) +
    lines(x + 4, y + imageH + 11, w - 8, 2, 3.5)
  );
}

// --- The archetypes ------------------------------------------------
// Each returns the body of the SVG. Keep them short: a thumbnail that needs
// twenty shapes to read is a thumbnail nobody can read at 96px.

const ART: Record<string, () => string> = {
  hero: () =>
    rect({ x: 0, y: 0, w: W, h: H, r: 0, o: 0.05 }) +
    heading(W / 2 - 22, 16, 44, 6) +
    lines(W / 2 - 26, 27, 52, 2, 4) +
    button(W / 2 - 9, 40),

  heroSplit: () =>
    rect({ x: 0, y: 0, w: W, h: H, r: 0, o: 0.05 }) +
    heading(8, 16, 34, 5) +
    lines(8, 25, 36, 2, 4) +
    button(8, 38, 16) +
    rect({ x: 52, y: 14, w: 36, h: 32, r: 2, o: 0.2 }),

  heroImage: () =>
    rect({ x: 0, y: 0, w: W, h: H, r: 0, o: 0.24 }) +
    heading(W / 2 - 22, 20, 44, 6, 0.5) +
    lines(W / 2 - 20, 31, 40, 1, 4, 0.4) +
    button(W / 2 - 9, 40),

  featureGrid: () => {
    const cols = columns(3);
    return (
      heading(W / 2 - 16, 8, 32, 4) +
      cols
        .map(
          (c) =>
            rect({ x: c.x, y: 20, w: 8, h: 8, r: 2, accent: true }) +
            heading(c.x, 32, c.w * 0.8, 3) +
            lines(c.x, 39, c.w, 2, 3.5),
        )
        .join('')
    );
  },

  cardGrid: () => {
    const cols = columns(3);
    return heading(8, 8, 32, 4) + cols.map((c) => card(c.x, 18, c.w, 34)).join('');
  },

  twoUp: () => {
    const cols = columns(2);
    return heading(8, 8, 32, 4) + cols.map((c) => card(c.x, 18, c.w, 34)).join('');
  },

  fourUp: () => {
    const cols = columns(4);
    return heading(8, 8, 28, 4) + cols.map((c) => card(c.x, 18, c.w, 34)).join('');
  },

  featureList: () =>
    rect({ x: 8, y: 8, w: 34, h: 20, r: 2, o: 0.2 }) +
    heading(48, 10, 26, 4) +
    lines(48, 18, 40, 2, 4) +
    rect({ x: 54, y: 34, w: 34, h: 20, r: 2, o: 0.2 }) +
    heading(8, 36, 26, 4) +
    lines(8, 44, 38, 2, 4),

  list: () =>
    [0, 1, 2].
      map((i) => {
        const y = 10 + i * 16;
        return (
          heading(8, y, 46, 4) +
          lines(8, y + 7, 74, 1, 4) +
          rect({ x: 8, y: y + 12, w: W - 16, h: 0.6, r: 0, o: 0.14 })
        );
      })
      .join(''),

  compactList: () =>
    [0, 1, 2]
      .map((i) => {
        const y = 8 + i * 17;
        return (
          rect({ x: 8, y, w: 18, h: 13, r: 2, o: 0.2 }) +
          heading(30, y + 1, 40, 3.5) +
          lines(30, y + 8, 52, 1, 4)
        );
      })
      .join(''),

  tiles: () => {
    const cols = columns(3, 6, W - 12, 3);
    return cols
      .map((c) => rect({ x: c.x, y: 8, w: c.w, h: 20, r: 2, o: 0.2 }) + rect({ x: c.x, y: 31, w: c.w, h: 20, r: 2, o: 0.2 }))
      .join('');
  },

  gallery: () => {
    const cols = columns(2, 10, W - 20, 4);
    return cols
      .map((c) => rect({ x: c.x, y: 8, w: c.w, h: 20, r: 2, o: 0.2 }) + rect({ x: c.x, y: 32, w: c.w, h: 20, r: 2, o: 0.2 }))
      .join('');
  },

  pricing: () => {
    const cols = columns(3);
    return cols
      .map((c, i) => {
        const y = i === 1 ? 8 : 13;
        const h = i === 1 ? 44 : 34;
        return (
          rect({ x: c.x, y, w: c.w, h, r: 2, o: i === 1 ? 0.16 : 0.1 }) +
          heading(c.x + 3, y + 4, c.w - 12, 3) +
          heading(c.x + 3, y + 10, c.w * 0.5, 6, 0.45) +
          lines(c.x + 3, y + 21, c.w - 6, 2, 3.5) +
          rect({ x: c.x + 3, y: y + h - 8, w: c.w - 6, h: 5, r: 2.5, accent: i === 1 })
        );
      })
      .join('');
  },

  stats: () =>
    columns(4)
      .map((c) => heading(c.x + c.w / 2 - 7, 22, 14, 8, 0.45) + lines(c.x + c.w / 2 - 8, 36, 16, 1, 4))
      .join(''),

  team: () =>
    columns(4)
      .map(
        (c) =>
          circle(c.x + c.w / 2, 22, 8) +
          heading(c.x + c.w / 2 - 7, 36, 14, 3) +
          lines(c.x + c.w / 2 - 5, 43, 10, 1, 4),
      )
      .join(''),

  timeline: () =>
    rect({ x: 14, y: 8, w: 1, h: 44, r: 0, o: 0.18 }) +
    [0, 1, 2]
      .map((i) => {
        const y = 12 + i * 15;
        return circle(14.5, y, 3, true) + heading(24, y - 4, 30, 3.5) + lines(24, y + 2, 58, 1, 4);
      })
      .join(''),

  steps: () =>
    columns(3)
      .map(
        (c, i) =>
          circle(c.x + 7, 18, 7, i === 0) +
          heading(c.x, 32, c.w * 0.8, 3.5) +
          lines(c.x, 39, c.w, 2, 3.5),
      )
      .join(''),

  faq: () =>
    [0, 1, 2]
      .map((i) => {
        const y = 10 + i * 14;
        return (
          rect({ x: 8, y, w: W - 16, h: 10, r: 2, o: i === 0 ? 0.14 : 0.08 }) +
          heading(12, y + 3.5, 44, 3) +
          rect({ x: W - 16, y: y + 4.5, w: 4, h: 1.5, r: 1, o: 0.3 })
        );
      })
      .join(''),

  table: () =>
    rect({ x: 8, y: 10, w: W - 16, h: 8, r: 1, o: 0.16 }) +
    [0, 1, 2]
      .map(
        (i) =>
          lines(11, 24 + i * 10, 22, 1, 4) +
          lines(44, 24 + i * 10, 14, 1, 4) +
          lines(68, 24 + i * 10, 14, 1, 4),
      )
      .join('') +
    rect({ x: 40, y: 10, w: 0.6, h: 40, r: 0, o: 0.12 }) +
    rect({ x: 64, y: 10, w: 0.6, h: 40, r: 0, o: 0.12 }),

  cta: () =>
    rect({ x: 0, y: 8, w: W, h: 44, r: 0, accent: true }) +
    rect({ x: W / 2 - 20, y: 20, w: 40, h: 5, r: 1, o: 0.55 }) +
    rect({ x: W / 2 - 14, y: 30, w: 28, h: 2, r: 1, o: 0.4 }) +
    rect({ x: W / 2 - 9, y: 38, w: 18, h: 6, r: 3, o: 0.75 }),

  logos: () =>
    lines(W / 2 - 12, 16, 24, 1, 4) +
    [0, 1, 2, 3].map((i) => rect({ x: 12 + i * 19, y: 28, w: 14, h: 8, r: 2, o: 0.18 })).join(''),

  quote: () =>
    rect({ x: 0, y: 0, w: W, h: H, r: 0, o: 0.05 }) +
    rect({ x: 20, y: 16, w: 3, h: 8, r: 1, accent: true }) +
    lines(28, 18, 48, 3, 5, 0.3) +
    lines(28, 40, 22, 1, 4, 0.2),

  quoteCards: () => {
    const cols = columns(2);
    return cols
      .map(
        (c) =>
          rect({ x: c.x, y: 12, w: c.w, h: 36, r: 2, o: 0.1 }) +
          lines(c.x + 4, 18, c.w - 8, 3, 4) +
          circle(c.x + 8, 40, 4) +
          heading(c.x + 15, 38, c.w * 0.4, 3),
      )
      .join('');
  },

  article: () =>
    heading(14, 8, 52, 6, 0.42) +
    lines(14, 19, 20, 1, 4, 0.2) +
    rect({ x: 14, y: 26, w: W - 28, h: 14, r: 2, o: 0.2 }) +
    lines(14, 44, W - 28, 3, 4),

  media: () =>
    rect({ x: 10, y: 10, w: W - 20, h: 40, r: 2, o: 0.18 }) +
    `<path d="M${W / 2 - 4} 24 L${W / 2 + 7} 30 L${W / 2 - 4} 36 Z" class="dcms-thumb-accent"/>`,

  downloads: () =>
    [0, 1, 2]
      .map((i) => {
        const y = 12 + i * 14;
        return (
          rect({ x: 8, y, w: W - 16, h: 10, r: 2, o: 0.09 }) +
          `<path d="M14 ${y + 3} v4 m-2 -2 l2 2 l2 -2" stroke="currentColor" stroke-width="1.2" fill="none" opacity="0.45" stroke-linecap="round"/>` +
          heading(22, y + 4, 40, 3)
        );
      })
      .join(''),

  navbar: () =>
    rect({ x: 0, y: 8, w: W, h: 14, r: 0, o: 0.1 }) +
    rect({ x: 8, y: 12, w: 16, h: 6, r: 1.5, accent: true }) +
    [0, 1, 2].map((i) => rect({ x: 48 + i * 14, y: 14, w: 10, h: 2.5, r: 1, o: 0.3 })).join('') +
    lines(8, 32, W - 16, 3, 5, 0.12),

  footer: () =>
    lines(8, 8, W - 16, 2, 5, 0.1) +
    rect({ x: 0, y: 24, w: W, h: 36, r: 0, o: 0.1 }) +
    columns(3, 8, W - 16)
      .map((c) => heading(c.x, 30, c.w * 0.6, 3) + lines(c.x, 37, c.w * 0.85, 3, 4, 0.18))
      .join(''),

  form: () =>
    heading(12, 8, 30, 4) +
    [0, 1]
      .map((i) => rect({ x: 12, y: 18 + i * 13, w: W - 24, h: 9, r: 2, o: 0.12 }))
      .join('') +
    button(12, 44, 22),

  tabs: () =>
    [0, 1, 2]
      .map((i) => rect({ x: 8 + i * 24, y: 10, w: 22, h: 8, r: 2, o: i === 0 ? 0.2 : 0.09 }))
      .join('') +
    rect({ x: 8, y: 22, w: W - 16, h: 28, r: 2, o: 0.07 }) +
    lines(14, 30, W - 28, 3, 5),

  carousel: () =>
    rect({ x: -8, y: 12, w: 24, h: 36, r: 2, o: 0.08 }) +
    rect({ x: 22, y: 12, w: 52, h: 36, r: 2, o: 0.18 }) +
    rect({ x: 80, y: 12, w: 24, h: 36, r: 2, o: 0.08 }) +
    [0, 1, 2].map((i) => circle(42 + i * 6, 54, 1.6, i === 1)).join(''),

  columns2: () =>
    columns(2)
      .map((c) => rect({ x: c.x, y: 10, w: c.w, h: 40, r: 2, o: 0.12 }))
      .join(''),

  columns3: () =>
    columns(3)
      .map((c) => rect({ x: c.x, y: 10, w: c.w, h: 40, r: 2, o: 0.12 }))
      .join(''),

  gridBoxes: () => {
    const cols = columns(3, 8, W - 16, 3);
    return cols
      .map((c) => rect({ x: c.x, y: 10, w: c.w, h: 18, r: 2, o: 0.12 }) + rect({ x: c.x, y: 32, w: c.w, h: 18, r: 2, o: 0.12 }))
      .join('');
  },

  stack: () =>
    [0, 1, 2].map((i) => rect({ x: 12, y: 12 + i * 14, w: W - 24, h: 10, r: 2, o: 0.13 })).join(''),

  section: () =>
    rect({ x: 4, y: 6, w: W - 8, h: 48, r: 2, o: 0.07 }) +
    heading(12, 14, 36, 4) +
    lines(12, 24, W - 24, 3, 5),

  spacer: () =>
    rect({ x: 8, y: 14, w: W - 16, h: 2, r: 1, o: 0.2 }) +
    rect({ x: 8, y: 44, w: W - 16, h: 2, r: 1, o: 0.2 }) +
    `<path d="M${W / 2} 22 v16 m-3 -13 l3 -3 l3 3 m-6 10 l3 3 l3 -3" stroke="currentColor" stroke-width="1.2" fill="none" opacity="0.35" stroke-linecap="round"/>`,

  divider: () => rect({ x: 8, y: H / 2 - 1, w: W - 16, h: 2, r: 1, o: 0.25 }),

  heading: () => heading(12, 18, 52, 8, 0.42) + lines(12, 34, W - 24, 2, 5),

  text: () => lines(12, 16, W - 24, 5, 6),

  button: () => rect({ x: W / 2 - 16, y: H / 2 - 6, w: 32, h: 12, r: 6, accent: true }),

  badge: () => rect({ x: W / 2 - 13, y: H / 2 - 5, w: 26, h: 10, r: 5, accent: true }),

  card: () => card(24, 8, 48, 44),

  person: () => circle(W / 2, 22, 10) + heading(W / 2 - 12, 38, 24, 4) + lines(W / 2 - 8, 47, 16, 1, 4),

  stat: () => heading(W / 2 - 14, 20, 28, 10, 0.45) + lines(W / 2 - 10, 38, 20, 1, 4),

  plan: () =>
    rect({ x: 26, y: 6, w: 44, h: 48, r: 2, o: 0.12 }) +
    heading(31, 11, 22, 3) +
    heading(31, 18, 18, 7, 0.45) +
    lines(31, 30, 34, 3, 4) +
    rect({ x: 31, y: 45, w: 34, h: 5, r: 2.5, accent: true }),

  step: () => circle(20, H / 2, 8, true) + heading(34, H / 2 - 7, 30, 4) + lines(34, H / 2 + 1, 46, 2, 4),

  eyebrow: () => rect({ x: 12, y: 16, w: 18, h: 3, r: 1.5, accent: true }) + heading(12, 24, 44, 5) + lines(12, 34, W - 24, 2, 5),

  timelineItem: () =>
    circle(14, 16, 3, true) +
    rect({ x: 14, y: 20, w: 1, h: 26, r: 0, o: 0.18 }) +
    heading(24, 12, 30, 4) +
    lines(24, 21, 56, 2, 4),

  image: () =>
    rect({ x: 10, y: 10, w: W - 20, h: 40, r: 2, o: 0.14 }) +
    circle(28, 24, 4, false, 0.3) +
    `<path d="M18 44 L36 28 L50 40 L62 32 L78 44 Z" opacity="0.28"/>`,

  embed: () =>
    rect({ x: 10, y: 10, w: W - 20, h: 40, r: 2, o: 0.1 }) +
    `<path d="M34 24 l-6 6 l6 6 m28 -12 l6 6 l-6 6" stroke="currentColor" stroke-width="1.6" fill="none" opacity="0.4" stroke-linecap="round" stroke-linejoin="round"/>`,

  map: () =>
    rect({ x: 10, y: 8, w: W - 20, h: 44, r: 2, o: 0.1 }) +
    `<path d="M20 46 L38 14 L58 46 L76 14" stroke="currentColor" stroke-width="1.4" fill="none" opacity="0.25"/>` +
    circle(48, 26, 4, true),

  contact: () =>
    heading(12, 10, 30, 4) +
    lines(12, 20, 34, 3, 6) +
    rect({ x: 54, y: 12, w: 32, h: 34, r: 2, o: 0.14 }),

  /** The generic data-bound block: cards with a small "live" mark. */
  plugin: () => {
    const cols = columns(2, 10, W - 20, 5);
    return (
      heading(10, 8, 30, 4) +
      circle(W - 14, 10, 2.5, true) +
      cols.map((c) => card(c.x, 18, c.w, 34)).join('')
    );
  },
};

/** Spec type → archetype, where the type deserves a drawing of its own. */
const BY_TYPE: Record<string, string> = {
  Hero: 'hero',
  HeroSplit: 'heroSplit',
  FeatureGrid: 'featureGrid',
  FeatureList: 'featureList',
  CardGrid: 'cardGrid',
  CallToAction: 'cta',
  PricingTable: 'pricing',
  Testimonials: 'quoteCards',
  QuoteBand: 'quote',
  TeamGrid: 'team',
  StatsBand: 'stats',
  LogoStrip: 'logos',
  Faq: 'faq',
  Timeline: 'timeline',
  Steps: 'steps',
  ComparisonTable: 'table',
  ContactBlock: 'contact',
  MapEmbed: 'map',
  Section: 'section',
  Container: 'section',
  Row: 'columns2',
  Column: 'columns2',
  Grid: 'gridBoxes',
  Stack: 'stack',
  Spacer: 'spacer',
  Divider: 'divider',
  Heading: 'heading',
  Paragraph: 'text',
  RichText: 'text',
  Blockquote: 'quote',
  List: 'list',
  CodeBlock: 'text',
  Badge: 'badge',
  Image: 'image',
  Figure: 'image',
  Video: 'media',
  Audio: 'media',
  MediaGallery: 'gallery',
  Navbar: 'navbar',
  Menu: 'stack',
  Tabs: 'tabs',
  Accordion: 'faq',
  Footer: 'footer',
  Sidebar: 'columns2',
  Button: 'button',
  ButtonGroup: 'button',
  Carousel: 'carousel',
  Modal: 'card',
  Form: 'form',
  NewsletterSignup: 'form',
  Embed: 'embed',
  RawHtml: 'embed',
  CustomCode: 'embed',
  SectionHead: 'eyebrow',
  Eyebrow: 'eyebrow',
  Card: 'card',
  Feature: 'step',
  PricingPlan: 'plan',
  TeamMember: 'person',
  Stat: 'stat',
  Testimonial: 'quote',
  Step: 'step',
  TimelineItem: 'timelineItem',
  FaqItem: 'faq',
  Slide: 'image',
  Actions: 'button',
};

/** Icon name → archetype, for specs with no entry of their own. */
const BY_ICON: Record<string, string> = {
  hero: 'hero',
  features: 'featureGrid',
  cta: 'cta',
  pricing: 'pricing',
  testimonial: 'quoteCards',
  team: 'team',
  stats: 'stats',
  logos: 'logos',
  faq: 'faq',
  timeline: 'timeline',
  steps: 'steps',
  comparison: 'table',
  contact: 'contact',
  map: 'map',
  card: 'card',
  plan: 'plan',
  person: 'person',
  stat: 'stat',
  step: 'step',
  slide: 'image',
  eyebrow: 'eyebrow',
  article: 'article',
  list: 'list',
  gallery: 'gallery',
  image: 'image',
  video: 'media',
  audio: 'media',
  download: 'downloads',
  form: 'form',
  input: 'form',
  select: 'form',
  checkbox: 'form',
  navbar: 'navbar',
  footer: 'footer',
  menu: 'stack',
  tabs: 'tabs',
  accordion: 'faq',
  carousel: 'carousel',
  grid: 'gridBoxes',
  row: 'columns2',
  stack: 'stack',
  spacer: 'spacer',
  divider: 'divider',
  heading: 'heading',
  text: 'text',
  quote: 'quote',
  badge: 'badge',
  button: 'button',
  embed: 'embed',
  raw: 'embed',
  section: 'section',
  container: 'section',
  plugin: 'plugin',
};

const BY_CATEGORY: Record<DcmsComponentSpec['category'], string> = {
  layout: 'section',
  typography: 'text',
  media: 'image',
  navigation: 'navbar',
  section: 'section',
  part: 'card',
  interactive: 'button',
  form: 'form',
  utility: 'embed',
  plugin: 'plugin',
  custom: 'card',
};

/** The layout a generated plugin block defaults to → its archetype. */
const BY_LAYOUT: Record<string, string> = {
  cards: 'plugin',
  tiles: 'tiles',
  list: 'list',
  compact: 'compactList',
  feature: 'featureList',
  article: 'article',
  video: 'media',
  audio: 'media',
  downloads: 'downloads',
};

/** Which drawing this spec gets. Exported for the tests, and for the panel. */
export function archetypeFor(spec: DcmsComponentSpec): string {
  if (spec.category === 'plugin') {
    const layout = spec.traits.find((t) => t.name === 'layout')?.default;
    const byLayout = typeof layout === 'string' ? BY_LAYOUT[layout] : undefined;
    if (byLayout) return byLayout;
  }
  return (
    BY_TYPE[spec.type] ??
    (spec.icon ? BY_ICON[spec.icon] : undefined) ??
    BY_CATEGORY[spec.category] ??
    'section'
  );
}

/**
 * The palette thumbnail for a spec, as a standalone SVG string.
 *
 * A string rather than a component because the block library must stay usable by
 * a headless editor in a test, and because GrapesJS's BlockManager carries block
 * media as markup — the same reason `icons.ts` returns strings.
 */
export function thumbnailFor(spec: DcmsComponentSpec): string {
  return thumbnail(archetypeFor(spec));
}

export function thumbnail(archetype: string): string {
  const draw = ART[archetype] ?? ART.section!;
  return (
    `<svg viewBox="0 0 ${W} ${H}" width="100%" height="100%" fill="currentColor" ` +
    `preserveAspectRatio="xMidYMid meet" role="presentation">${draw()}</svg>`
  );
}

/** Every archetype that can be drawn. Used by the tests to render them all. */
export const THUMBNAIL_ARCHETYPES = Object.keys(ART);
