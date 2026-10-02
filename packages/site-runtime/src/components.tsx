import type { AnchorHTMLAttributes, ReactNode } from 'react';
import type { Action } from './actions';
import type { PropDefinition } from './props';
import { createRegistry, type ComponentDefinition, type ComponentRenderProps } from './registry';
import { useRenderMode } from './renderMode';

/**
 * The built-in components.
 *
 * Every option a prop offers maps to a fixed class, never to a value copied into a style:
 * `gap: "md"` becomes `dcms-gap-md`, and the class reads the theme token. That keeps every
 * look inside the design kit, and keeps a value that did not come through the editor — a
 * hand-edited file, a model's tool call — from reaching CSS at all: an unknown value falls
 * back to the default (`choice`).
 */

type Props = Record<string, unknown>;

function choice<T extends string>(value: unknown, allowed: readonly T[], fallback: T): T {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value) ? (value as T) : fallback;
}

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback;
}

function select<T extends string>(name: string, label: string, values: readonly T[], fallback: T, labels?: Partial<Record<T, string>>) {
  return {
    kind: 'select' as const,
    name,
    label,
    options: values.map((value) => ({ value, label: labels?.[value] ?? value })),
    default: fallback,
  } satisfies PropDefinition;
}

const WIDTHS = ['narrow', 'normal', 'wide', 'full'] as const;
const SPACES = ['none', 'xs', 'sm', 'md', 'lg', 'xl'] as const;
const ALIGNS = ['start', 'center', 'end'] as const;

// ---------------------------------------------------------------------------

function Page({ slot }: ComponentRenderProps) {
  return <main className="dcms-page">{slot('default')}</main>;
}

const BACKGROUNDS = ['none', 'alt', 'soft', 'inverse'] as const;
const SECTION_SPACES = ['none', 'sm', 'md', 'lg'] as const;

function Section({ props, slot }: ComponentRenderProps<Props>) {
  const bg = choice(props.background, BACKGROUNDS, 'none');
  const py = choice(props.spacing, SECTION_SPACES, 'md');
  const width = choice(props.width, WIDTHS, 'normal');
  return (
    <section className={`dcms-section dcms-bg-${bg} dcms-py-${py}`}>
      {slot('default', { className: `dcms-width dcms-width-${width} dcms-flow` })}
    </section>
  );
}

function Container({ props, slot }: ComponentRenderProps<Props>) {
  const width = choice(props.width, WIDTHS, 'normal');
  return slot('default', { className: `dcms-width dcms-width-${width} dcms-flow` });
}

const DIRECTIONS = ['vertical', 'horizontal'] as const;
const STACK_ALIGNS = ['start', 'center', 'end', 'stretch'] as const;
const JUSTIFIES = ['start', 'center', 'end', 'between'] as const;

function Stack({ props, slot }: ComponentRenderProps<Props>) {
  const direction = choice(props.direction, DIRECTIONS, 'vertical');
  const gap = choice(props.gap, SPACES, 'md');
  const align = choice(props.align, STACK_ALIGNS, 'stretch');
  const justify = choice(props.justify, JUSTIFIES, 'start');
  const wrap = props.wrap === true ? ' dcms-wrap' : '';
  return slot('default', {
    className: `dcms-stack dcms-stack-${direction} dcms-gap-${gap} dcms-align-${align} dcms-justify-${justify}${wrap}`,
  });
}

const LEVELS = ['1', '2', '3', '4', '5', '6'] as const;

function Heading({ props }: ComponentRenderProps<Props>) {
  const level = choice(props.level, LEVELS, '2');
  const align = choice(props.align, ALIGNS, 'start');
  const Tag = `h${level}` as 'h1';
  return <Tag className={`dcms-heading dcms-heading-${level} dcms-text-${align}`}>{text(props.text)}</Tag>;
}

const SIZES = ['sm', 'base', 'lg'] as const;
const TONES = ['default', 'muted'] as const;

function Text({ props }: ComponentRenderProps<Props>) {
  const size = choice(props.size, SIZES, 'base');
  const align = choice(props.align, ALIGNS, 'start');
  const tone = choice(props.tone, TONES, 'default');
  return <p className={`dcms-text dcms-size-${size} dcms-text-${align} dcms-tone-${tone}`}>{text(props.text)}</p>;
}

const FITS = ['cover', 'contain'] as const;
const RATIOS = ['auto', '16-9', '4-3', '1-1', '3-4'] as const;
const RADII = ['none', 'sm', 'lg'] as const;

function Image({ props }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  // ponytail: the value is used as a URL until media props resolve MediaRefs (P2.7).
  const src = text(props.src);
  const ratio = choice(props.ratio, RATIOS, 'auto');
  const fit = choice(props.fit, FITS, 'cover');
  const radius = choice(props.radius, RADII, 'none');
  const className = `dcms-image dcms-ratio-${ratio} dcms-fit-${fit} dcms-radius-${radius}`;
  if (!src) return mode === 'edit' ? <div className={`${className} dcms-image-empty`}>Choose an image</div> : null;
  return <img className={className} src={src} alt={text(props.alt)} loading="lazy" />;
}

const VARIANTS = ['primary', 'secondary', 'ghost'] as const;
const BUTTON_SIZES = ['sm', 'md', 'lg'] as const;

/**
 * The link an action makes, when it is one. Anything else (a modal, a toast, a form) is run by
 * the action runner, which is not wired yet; until then those buttons render inert.
 */
function linkFor(action: Action | undefined): AnchorHTMLAttributes<HTMLAnchorElement> {
  if (action?.type === 'navigate') return { href: action.to };
  if (action?.type === 'open-external') {
    return action.newTab ? { href: action.href, target: '_blank', rel: 'noopener noreferrer' } : { href: action.href };
  }
  return {};
}

function Button({ props, action }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const variant = choice(props.variant, VARIANTS, 'primary');
  const size = choice(props.size, BUTTON_SIZES, 'md');
  const className = `dcms-button dcms-button-${variant} dcms-button-${size}`;
  const label: ReactNode = text(props.label, 'Button');
  // On the canvas a click selects. A real href would let a click navigate the editor's own
  // frame away from the page being edited, so none is rendered there at all.
  const link = mode === 'edit' ? {} : linkFor(action);
  return link.href ? (
    <a className={className} {...link}>
      {label}
    </a>
  ) : (
    <button type="button" className={className}>
      {label}
    </button>
  );
}

// ---------------------------------------------------------------------------

const alignProp = select('align', 'Alignment', ALIGNS, 'start');

export const BUILTIN_COMPONENTS: readonly ComponentDefinition[] = [
  {
    type: 'dcms.page',
    version: 1,
    label: 'Page',
    category: 'Structure',
    component: Page,
    props: [],
    slots: [{ name: 'default', label: 'Content' }],
    // The root of every page document; it exists only where the platform puts it.
    allowedParents: [],
    draggable: false,
  },
  {
    type: 'dcms.section',
    version: 1,
    label: 'Section',
    description: 'A full-width band of the page with its own background and spacing.',
    category: 'Layout',
    component: Section,
    props: [
      select('background', 'Background', BACKGROUNDS, 'none'),
      select('spacing', 'Vertical spacing', SECTION_SPACES, 'md'),
      select('width', 'Content width', WIDTHS, 'normal'),
    ],
    slots: [{ name: 'default', label: 'Content' }],
  },
  {
    type: 'dcms.container',
    version: 1,
    label: 'Container',
    description: 'Centres its content at a readable width.',
    category: 'Layout',
    component: Container,
    props: [select('width', 'Width', WIDTHS, 'normal')],
    slots: [{ name: 'default', label: 'Content' }],
  },
  {
    type: 'dcms.stack',
    version: 1,
    label: 'Stack',
    description: 'Lays its children out in a row or a column with even spacing.',
    category: 'Layout',
    component: Stack,
    props: [
      select('direction', 'Direction', DIRECTIONS, 'vertical'),
      select('gap', 'Gap', SPACES, 'md'),
      select('align', 'Align items', STACK_ALIGNS, 'stretch'),
      select('justify', 'Justify', JUSTIFIES, 'start'),
      { kind: 'boolean', name: 'wrap', label: 'Wrap onto new lines', default: false },
    ],
    slots: [{ name: 'default', label: 'Items' }],
  },
  {
    type: 'dcms.heading',
    version: 1,
    label: 'Heading',
    category: 'Content',
    component: Heading,
    props: [
      { kind: 'text', name: 'text', label: 'Text', default: 'Heading', maxLength: 300 },
      select('level', 'Level', LEVELS, '2'),
      alignProp,
    ],
  },
  {
    type: 'dcms.text',
    version: 1,
    label: 'Text',
    category: 'Content',
    component: Text,
    props: [
      { kind: 'text', name: 'text', label: 'Text', default: 'Write something here.', multiline: true, maxLength: 5000 },
      select('size', 'Size', SIZES, 'base'),
      select('tone', 'Tone', TONES, 'default'),
      alignProp,
    ],
  },
  {
    type: 'dcms.image',
    version: 1,
    label: 'Image',
    category: 'Media',
    component: Image,
    props: [
      { kind: 'media', name: 'src', label: 'Image' },
      { kind: 'text', name: 'alt', label: 'Description (alt text)', maxLength: 300 },
      select('ratio', 'Aspect ratio', RATIOS, 'auto'),
      select('fit', 'Fit', FITS, 'cover'),
      select('radius', 'Corners', RADII, 'none'),
    ],
  },
  {
    type: 'dcms.button',
    version: 1,
    label: 'Button',
    category: 'Content',
    component: Button,
    props: [
      { kind: 'text', name: 'label', label: 'Label', default: 'Button', maxLength: 80 },
      select('variant', 'Style', VARIANTS, 'primary'),
      select('size', 'Size', BUTTON_SIZES, 'md'),
    ],
  },
];

export const builtinRegistry = createRegistry(BUILTIN_COMPONENTS);

/** A new instance's props: every declared default. */
export function defaultProps(definition: ComponentDefinition): Record<string, unknown> {
  const props: Record<string, unknown> = {};
  for (const prop of definition.props) {
    if ('default' in prop && prop.default !== undefined) props[prop.name] = prop.default;
  }
  return props;
}
