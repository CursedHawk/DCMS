import { createElement } from 'react';
import { ICONS } from './icons';
import { choice, select, text, variants, type Props } from './kit';
import type { ComponentDefinition, ComponentRenderProps } from './registry';
import { useRenderMode } from './renderMode';
import { SiteLink } from './site';

/**
 * Content primitives (Mode D v2, U1.1): card, icon, badge, list, divider, quote. Like every
 * built-in, an option is a class, never a value copied into a style.
 */

/** An icon from the embedded set, on lucide's 24px stroke grid. Decorative unless labelled. */
export function IconGlyph({ name, label, className }: { name: string; label?: string; className?: string }) {
  const nodes = ICONS[name];
  if (!nodes) return null;
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      role={label ? 'img' : undefined}
      aria-label={label || undefined}
      aria-hidden={label ? undefined : true}
    >
      {nodes.map(([tag, attrs], i) => createElement(tag, { key: i, ...attrs }))}
    </svg>
  );
}

const ICON_SIZES = ['sm', 'md', 'lg', 'xl'] as const;
const ICON_TONES = ['current', 'brand', 'muted'] as const;
const ICON_SHAPES = ['plain', 'circle', 'square'] as const;

function Icon({ props, responsive }: ComponentRenderProps<Props>) {
  const v = variants(props, responsive);
  const size = v('size', ICON_SIZES, 'md', (x) => `dcms-icon-${x}`);
  const tone = choice(props.tone, ICON_TONES, 'brand');
  const shape = choice(props.shape, ICON_SHAPES, 'plain');
  const name = typeof props.icon === 'string' && props.icon in ICONS ? props.icon : 'star';
  return (
    <span className={`dcms-icon ${size} dcms-icon-tone-${tone} dcms-icon-shape-${shape}`}>
      <IconGlyph name={name} label={text(props.label).trim() || undefined} />
    </span>
  );
}

const BADGE_TONES = ['brand', 'neutral', 'success', 'warning', 'danger'] as const;

function Badge({ props }: ComponentRenderProps<Props>) {
  const tone = choice(props.tone, BADGE_TONES, 'brand');
  return <span className={`dcms-badge dcms-badge-${tone}`}>{text(props.text, 'New')}</span>;
}

const MARKERS = ['bullet', 'number', 'check', 'icon', 'none'] as const;
const LIST_GAPS = ['sm', 'md', 'lg'] as const;

function List({ props }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const marker = choice(props.marker, MARKERS, 'check');
  const gap = choice(props.gap, LIST_GAPS, 'sm');
  const items = text(props.items)
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean);
  const icon = marker === 'check' ? 'check' : typeof props.icon === 'string' && props.icon in ICONS ? props.icon : 'arrow-right';
  const Tag = marker === 'number' ? 'ol' : 'ul';
  // An empty list is invisible; on the canvas it says how to fill it.
  if (!items.length && mode === 'edit') return <p className="dcms-editor-note">List — add points, one per line, in the settings.</p>;
  return (
    <Tag className={`dcms-list dcms-list-${marker} dcms-list-gap-${gap}`}>
      {items.map((item, i) => (
        <li key={i}>
          {(marker === 'check' || marker === 'icon') && <IconGlyph name={icon} className="dcms-list-marker" />}
          <span>{item}</span>
        </li>
      ))}
    </Tag>
  );
}

const DIVIDER_STYLES = ['solid', 'dashed', 'dotted'] as const;
const DIVIDER_SPACES = ['sm', 'md', 'lg'] as const;
const DIVIDER_WIDTHS = ['full', 'short'] as const;

function Divider({ props }: ComponentRenderProps<Props>) {
  const style = choice(props.style, DIVIDER_STYLES, 'solid');
  const space = choice(props.spacing, DIVIDER_SPACES, 'md');
  const width = choice(props.width, DIVIDER_WIDTHS, 'full');
  return <hr className={`dcms-divider dcms-divider-${style} dcms-divider-space-${space} dcms-divider-${width}`} />;
}

const QUOTE_STYLES = ['plain', 'large', 'card'] as const;

function Quote({ props, responsive }: ComponentRenderProps<Props>) {
  const style = choice(props.style, QUOTE_STYLES, 'large');
  const align = variants(props, responsive)('align', ['start', 'center'] as const, 'start', (x) => `dcms-text-${x}`);
  const cite = text(props.cite).trim();
  return (
    <figure className={`dcms-quote dcms-quote-${style} ${align}`}>
      <blockquote>
        <p>{text(props.text, 'A sentence worth repeating.')}</p>
      </blockquote>
      {cite && <figcaption>{cite}</figcaption>}
    </figure>
  );
}

const CARD_VARIANTS = ['outline', 'raised', 'filled', 'plain'] as const;
const CARD_PADDINGS = ['sm', 'md', 'lg'] as const;

function Card({ props, action, slot }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const variant = choice(props.variant, CARD_VARIANTS, 'outline');
  const padding = choice(props.padding, CARD_PADDINGS, 'md');
  const label = text(props.linkLabel).trim();
  // A whole-card link: one stretched link under the content, so buttons inside stay buttons.
  const href = action?.type === 'navigate' ? action.to : action?.type === 'open-external' ? action.href : null;
  return (
    <article className={`dcms-card dcms-card-${variant}${href ? ' dcms-card-linked' : ''}`}>
      {slot('media', { className: 'dcms-card-media' })}
      <div className={`dcms-card-body dcms-pad-${padding}`}>{slot('default', { className: 'dcms-card-content' })}</div>
      {slot('footer', { className: `dcms-card-footer dcms-pad-${padding}` })}
      {href && mode === 'live' && (
        <SiteLink
          to={href}
          className="dcms-card-cover"
          aria-label={label || undefined}
          {...(action?.type === 'open-external' && action.newTab ? { target: '_blank', rel: 'noopener noreferrer' } : {})}
        />
      )}
    </article>
  );
}

const iconProp = (name: string, label: string, description: string, fallback = 'star') =>
  ({ kind: 'icon', name, label, default: fallback, group: 'content', description }) as const;

export const CONTENT_COMPONENTS: readonly ComponentDefinition[] = [
  {
    type: 'dcms.card',
    version: 1,
    label: 'Card',
    description: 'A framed box for one thing — a service, a product, a person — with room for a picture on top and buttons at the bottom.',
    category: 'Content',
    keywords: ['box', 'tile', 'panel', 'teaser', 'feature'],
    component: Card,
    actions: ['navigate', 'open-external'],
    props: [
      select('variant', 'Look', CARD_VARIANTS, 'outline', { outline: 'Outlined', raised: 'Raised (shadow)', filled: 'Filled', plain: 'No frame' }, {
        group: 'style',
        description: 'How the card stands out from the page behind it.',
      }),
      select('padding', 'Inner spacing', CARD_PADDINGS, 'md', { sm: 'Small', md: 'Medium', lg: 'Large' }, {
        group: 'layout',
        description: 'Room between the card’s edge and its content.',
      }),
      {
        kind: 'text',
        name: 'linkLabel',
        label: 'Link description',
        maxLength: 120,
        group: 'behaviour',
        description: 'When the whole card is a link (set what it does below): what screen readers announce for it.',
      },
    ],
    slots: [
      { name: 'media', label: 'Picture', max: 1 },
      { name: 'default', label: 'Content' },
      { name: 'footer', label: 'Bottom' },
    ],
  },
  {
    type: 'dcms.icon',
    version: 1,
    label: 'Icon',
    description: 'A small symbol — a phone, a map pin, a star — to make features and contact details quick to scan.',
    category: 'Media',
    keywords: ['symbol', 'glyph', 'pictogram', 'emoji'],
    component: Icon,
    props: [
      { ...iconProp('icon', 'Icon', 'Which symbol to show.'), default: 'star' },
      select('size', 'Size', ICON_SIZES, 'md', { sm: 'Small', md: 'Medium', lg: 'Large', xl: 'Huge' }, {
        group: 'style',
        description: 'How big the icon is.',
      }),
      select('tone', 'Colour', ICON_TONES, 'brand', { current: 'Like the text', brand: 'Brand colour', muted: 'Muted' }, {
        group: 'style',
        description: 'Which colour the icon is drawn in.',
      }),
      select('shape', 'Background', ICON_SHAPES, 'plain', { plain: 'None', circle: 'Circle', square: 'Rounded square' }, {
        group: 'style',
        description: 'A tinted shape behind the icon, for feature lists.',
      }),
      {
        kind: 'text',
        name: 'label',
        label: 'Meaning (for screen readers)',
        maxLength: 120,
        group: 'content',
        description: 'Only if the icon says something the text beside it does not. Leave empty when it is decoration.',
      },
    ],
  },
  {
    type: 'dcms.badge',
    version: 1,
    label: 'Badge',
    description: 'A short coloured label — “New”, “Sold out”, “Popular” — that draws the eye.',
    category: 'Content',
    keywords: ['label', 'tag', 'pill', 'chip', 'status'],
    component: Badge,
    props: [
      { kind: 'text', name: 'text', label: 'Text', default: 'New', maxLength: 40, group: 'content', description: 'One or two words.' },
      select('tone', 'Colour', BADGE_TONES, 'brand', { brand: 'Brand', neutral: 'Grey', success: 'Green', warning: 'Amber', danger: 'Red' }, {
        group: 'style',
        description: 'Green for good news, amber for caution, red for warnings.',
      }),
    ],
  },
  {
    type: 'dcms.list',
    version: 1,
    label: 'List',
    description: 'A list of short points, one per line — with bullets, numbers, ticks or an icon of your choice.',
    category: 'Content',
    keywords: ['bullets', 'points', 'checklist', 'numbered', 'features'],
    component: List,
    props: [
      {
        kind: 'text',
        name: 'items',
        label: 'Points',
        default: 'Free delivery\nTwo-year warranty\nFriendly support',
        multiline: true,
        maxLength: 5000,
        group: 'content',
        description: 'One point per line.',
      },
      select('marker', 'Marker', MARKERS, 'check', { bullet: 'Bullets', number: 'Numbers', check: 'Ticks', icon: 'An icon', none: 'None' }, {
        group: 'style',
        description: 'What sits in front of each point.',
      }),
      { ...iconProp('icon', 'Marker icon', 'The icon in front of each point, when the marker is “An icon”.', 'arrow-right'), group: 'style' },
      select('gap', 'Space between', LIST_GAPS, 'sm', { sm: 'Small', md: 'Medium', lg: 'Large' }, {
        group: 'layout',
        description: 'Room between the points.',
      }),
    ],
  },
  {
    type: 'dcms.divider',
    version: 1,
    label: 'Divider',
    description: 'A line that separates one part of the content from the next.',
    category: 'Layout',
    keywords: ['line', 'rule', 'separator', 'hr'],
    component: Divider,
    props: [
      select('style', 'Line', DIVIDER_STYLES, 'solid', { solid: 'Solid', dashed: 'Dashed', dotted: 'Dotted' }, {
        group: 'style',
        description: 'How the line is drawn.',
      }),
      select('width', 'Length', DIVIDER_WIDTHS, 'full', { full: 'Full width', short: 'Short' }, {
        group: 'style',
        description: 'A short line reads as a quiet accent under a heading.',
      }),
      select('spacing', 'Space around', DIVIDER_SPACES, 'md', { sm: 'Small', md: 'Medium', lg: 'Large' }, {
        group: 'layout',
        description: 'Room above and below the line.',
      }),
    ],
  },
  {
    type: 'dcms.quote',
    version: 1,
    label: 'Quote',
    description: 'Words from someone else — a review, a testimonial, a famous line — with who said it.',
    category: 'Content',
    keywords: ['testimonial', 'review', 'blockquote', 'citation', 'saying'],
    component: Quote,
    props: [
      {
        kind: 'text',
        name: 'text',
        label: 'Quote',
        default: 'A sentence worth repeating.',
        multiline: true,
        maxLength: 1000,
        group: 'content',
        description: 'The words, without quotation marks — the design adds them.',
      },
      { kind: 'text', name: 'cite', label: 'Who said it', maxLength: 160, group: 'content', description: 'A name, and a role or company if it helps.' },
      select('style', 'Look', QUOTE_STYLES, 'large', { plain: 'Plain', large: 'Large', card: 'In a card' }, {
        group: 'style',
        description: 'Large for a single standout quote, in a card for several side by side.',
      }),
      { ...select('align', 'Alignment', ['start', 'center'] as const, 'start', { start: 'Left', center: 'Centre' }, { group: 'style', description: 'Which side the quote lines up with.' }), responsive: true },
    ],
  },
];

