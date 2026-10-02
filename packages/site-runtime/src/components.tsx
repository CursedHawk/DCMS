import type { ReactNode } from 'react';
import { Outlet, useInRouterContext } from 'react-router';
import type { NavItem, Responsive } from './document';
import type { PropDefinition } from './props';
import { createRegistry, type ComponentDefinition, type ComponentRenderProps } from './registry';
import { useRenderMode } from './renderMode';
import { SiteLink, useSite } from './site';

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

/**
 * A responsive prop's classes: the desktop value's class, plus `t-`/`m-` prefixed ones for any
 * tablet or mobile override. Each value goes through `choice` like any other.
 */
function variants(props: Props, responsive: Responsive | undefined) {
  return <T extends string>(name: string, allowed: readonly T[], fallback: T, cls: (value: T) => string): string => {
    const parts = [cls(choice(props[name], allowed, fallback))];
    const tablet = responsive?.tablet?.[name];
    const mobile = responsive?.mobile?.[name];
    if (tablet !== undefined) parts.push(`t-${cls(choice(tablet, allowed, fallback))}`);
    if (mobile !== undefined) parts.push(`m-${cls(choice(mobile, allowed, fallback))}`);
    return parts.join(' ');
  };
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

/** A select whose value may differ on tablet and mobile (see `variants`). */
function responsiveSelect<T extends string>(name: string, label: string, values: readonly T[], fallback: T) {
  return { ...select(name, label, values, fallback), responsive: true } satisfies PropDefinition;
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

function Section({ props, responsive, slot }: ComponentRenderProps<Props>) {
  const v = variants(props, responsive);
  const bg = choice(props.background, BACKGROUNDS, 'none');
  const py = v('spacing', SECTION_SPACES, 'md', (x) => `dcms-py-${x}`);
  const width = v('width', WIDTHS, 'normal', (x) => `dcms-width-${x}`);
  return (
    <section className={`dcms-section dcms-bg-${bg} ${py}`}>
      {slot('default', { className: `dcms-width ${width} dcms-flow` })}
    </section>
  );
}

function Container({ props, responsive, slot }: ComponentRenderProps<Props>) {
  const width = variants(props, responsive)('width', WIDTHS, 'normal', (x) => `dcms-width-${x}`);
  return slot('default', { className: `dcms-width ${width} dcms-flow` });
}

const DIRECTIONS = ['vertical', 'horizontal'] as const;
const STACK_ALIGNS = ['start', 'center', 'end', 'stretch'] as const;
const JUSTIFIES = ['start', 'center', 'end', 'between'] as const;

function Stack({ props, responsive, slot }: ComponentRenderProps<Props>) {
  const v = variants(props, responsive);
  const direction = v('direction', DIRECTIONS, 'vertical', (x) => `dcms-stack-${x}`);
  const gap = v('gap', SPACES, 'md', (x) => `dcms-gap-${x}`);
  const align = v('align', STACK_ALIGNS, 'stretch', (x) => `dcms-align-${x}`);
  const justify = v('justify', JUSTIFIES, 'start', (x) => `dcms-justify-${x}`);
  const wrap = props.wrap === true ? ' dcms-wrap' : '';
  return slot('default', { className: `dcms-stack ${direction} ${gap} ${align} ${justify}${wrap}` });
}

const COLUMNS = ['1', '2', '3', '4', '5', '6'] as const;

function Grid({ props, responsive, slot }: ComponentRenderProps<Props>) {
  const v = variants(props, responsive);
  const columns = v('columns', COLUMNS, '3', (x) => `dcms-cols-${x}`);
  const gap = v('gap', SPACES, 'md', (x) => `dcms-gap-${x}`);
  return slot('default', { className: `dcms-grid ${columns} ${gap}` });
}

const SPLITS = ['1-1', '2-1', '1-2', 'stacked'] as const;

function Split({ props, responsive, slot }: ComponentRenderProps<Props>) {
  const ratio = variants(props, responsive)('ratio', SPLITS, '1-1', (x) => `dcms-split-${x}`);
  return (
    <div className={`dcms-split ${ratio}`}>
      {slot('start', { className: 'dcms-flow' })}
      {slot('end', { className: 'dcms-flow' })}
    </div>
  );
}

const SPACER_SIZES = ['xs', 'sm', 'md', 'lg', 'xl'] as const;

function Spacer({ props, responsive }: ComponentRenderProps<Props>) {
  const size = variants(props, responsive)('size', SPACER_SIZES, 'md', (x) => `dcms-spacer-${x}`);
  return <div className={`dcms-spacer ${size}`} aria-hidden="true" />;
}

const LEVELS = ['1', '2', '3', '4', '5', '6'] as const;

function Heading({ props, responsive }: ComponentRenderProps<Props>) {
  const level = choice(props.level, LEVELS, '2');
  const align = variants(props, responsive)('align', ALIGNS, 'start', (x) => `dcms-text-${x}`);
  const Tag = `h${level}` as 'h1';
  return <Tag className={`dcms-heading dcms-heading-${level} ${align}`}>{text(props.text)}</Tag>;
}

const SIZES = ['sm', 'base', 'lg'] as const;
const TONES = ['default', 'muted'] as const;

function Text({ props, responsive }: ComponentRenderProps<Props>) {
  const v = variants(props, responsive);
  const size = v('size', SIZES, 'base', (x) => `dcms-size-${x}`);
  const align = v('align', ALIGNS, 'start', (x) => `dcms-text-${x}`);
  const tone = choice(props.tone, TONES, 'default');
  return <p className={`dcms-text ${size} ${align} dcms-tone-${tone}`}>{text(props.text)}</p>;
}

const FITS = ['cover', 'contain'] as const;
const RATIOS = ['auto', '16-9', '4-3', '1-1', '3-4'] as const;
const RADII = ['none', 'sm', 'lg'] as const;

function Image({ props, responsive }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  // ponytail: the value is used as a URL until media props resolve MediaRefs (P2.7).
  const src = text(props.src);
  const ratio = variants(props, responsive)('ratio', RATIOS, 'auto', (x) => `dcms-ratio-${x}`);
  const fit = choice(props.fit, FITS, 'cover');
  const radius = choice(props.radius, RADII, 'none');
  const className = `dcms-image ${ratio} dcms-fit-${fit} dcms-radius-${radius}`;
  if (!src) return mode === 'edit' ? <div className={`${className} dcms-image-empty`}>Choose an image</div> : null;
  return <img className={className} src={src} alt={text(props.alt)} loading="lazy" />;
}

const VARIANTS = ['primary', 'secondary', 'ghost'] as const;
const BUTTON_SIZES = ['sm', 'md', 'lg'] as const;


/**
 * A button is a link when its action is one; anything else (a modal, a toast, a form) is run by
 * the action runner, which is not wired yet — until then those buttons render inert.
 */
function Button({ props, action }: ComponentRenderProps<Props>) {
  const variant = choice(props.variant, VARIANTS, 'primary');
  const size = choice(props.size, BUTTON_SIZES, 'md');
  const className = `dcms-button dcms-button-${variant} dcms-button-${size}`;
  const label: ReactNode = text(props.label, 'Button');
  if (action?.type === 'navigate') return <SiteLink to={action.to} className={className}>{label}</SiteLink>;
  if (action?.type === 'open-external') {
    return (
      <SiteLink
        to={action.href}
        className={className}
        {...(action.newTab ? { target: '_blank', rel: 'noopener noreferrer' } : {})}
      >
        {label}
      </SiteLink>
    );
  }
  return (
    <button type="button" className={className}>
      {label}
    </button>
  );
}

/** Where the route's page goes inside the app shell. On the canvas, a labelled placeholder. */
function PageOutlet() {
  const mode = useRenderMode();
  const inRouter = useInRouterContext();
  if (mode === 'edit' || !inRouter) return <div className="dcms-outlet-placeholder">Page content</div>;
  return <Outlet />;
}

const NAV_LAYOUTS = ['horizontal', 'vertical'] as const;

function NavList({ items }: { items: readonly NavItem[] }) {
  return (
    <ul className="dcms-nav-list">
      {items.map((item, i) => (
        <li key={`${i}:${item.to}`}>
          <SiteLink to={item.to} className="dcms-nav-link">
            {item.label}
          </SiteLink>
          {item.children?.length ? <NavList items={item.children} /> : null}
        </li>
      ))}
    </ul>
  );
}

/** One of the app's named menus (`app.json` → `navigation`), so every page shares it. */
function Navigation({ props }: ComponentRenderProps<Props>) {
  const { app } = useSite();
  const mode = useRenderMode();
  const menu = text(props.menu, 'main');
  const layout = choice(props.layout, NAV_LAYOUTS, 'horizontal');
  const items = app?.navigation?.[menu] ?? [];
  if (!items.length) return mode === 'edit' ? <div className="dcms-outlet-placeholder">Menu “{menu}” is empty</div> : null;
  return (
    <nav className={`dcms-nav dcms-nav-${layout}`} aria-label={menu}>
      <NavList items={items} />
    </nav>
  );
}

// ---------------------------------------------------------------------------

const alignProp = responsiveSelect('align', 'Alignment', ALIGNS, 'start');

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
    type: 'dcms.outlet',
    version: 1,
    label: 'Page content',
    description: 'Where each route draws its page, inside the app shell.',
    category: 'Structure',
    component: PageOutlet,
    props: [],
    // The shell has exactly one, and the platform puts it there.
    draggable: false,
  },
  {
    type: 'dcms.nav',
    version: 1,
    label: 'Navigation',
    description: 'One of the site’s menus. Edit the links once and every page that shows the menu follows.',
    category: 'Navigation',
    component: Navigation,
    props: [
      { kind: 'text', name: 'menu', label: 'Menu', default: 'main', maxLength: 64 },
      select('layout', 'Layout', NAV_LAYOUTS, 'horizontal'),
    ],
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
      responsiveSelect('spacing', 'Vertical spacing', SECTION_SPACES, 'md'),
      responsiveSelect('width', 'Content width', WIDTHS, 'normal'),
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
    props: [responsiveSelect('width', 'Width', WIDTHS, 'normal')],
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
      responsiveSelect('direction', 'Direction', DIRECTIONS, 'vertical'),
      responsiveSelect('gap', 'Gap', SPACES, 'md'),
      responsiveSelect('align', 'Align items', STACK_ALIGNS, 'stretch'),
      responsiveSelect('justify', 'Justify', JUSTIFIES, 'start'),
      { kind: 'boolean', name: 'wrap', label: 'Wrap onto new lines', default: false },
    ],
    slots: [{ name: 'default', label: 'Items' }],
  },
  {
    type: 'dcms.grid',
    version: 1,
    label: 'Grid',
    description: 'Equal columns that wrap onto new rows. Set fewer columns for tablet and mobile.',
    category: 'Layout',
    component: Grid,
    props: [responsiveSelect('columns', 'Columns', COLUMNS, '3'), responsiveSelect('gap', 'Gap', SPACES, 'md')],
    slots: [{ name: 'default', label: 'Items' }],
  },
  {
    type: 'dcms.split',
    version: 1,
    label: 'Split',
    description: 'Two columns side by side — text beside an image. Stacks on phones.',
    category: 'Layout',
    component: Split,
    props: [responsiveSelect('ratio', 'Columns', SPLITS, '1-1')],
    slots: [
      { name: 'start', label: 'First column' },
      { name: 'end', label: 'Second column' },
    ],
  },
  {
    type: 'dcms.spacer',
    version: 1,
    label: 'Spacer',
    category: 'Layout',
    component: Spacer,
    props: [responsiveSelect('size', 'Size', SPACER_SIZES, 'md')],
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
      responsiveSelect('size', 'Size', SIZES, 'base'),
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
      responsiveSelect('ratio', 'Aspect ratio', RATIOS, 'auto'),
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
    actions: ['navigate', 'open-external'],
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
