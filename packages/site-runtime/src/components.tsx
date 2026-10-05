import { useContext, useState, type ReactNode } from 'react';
import { Outlet, useInRouterContext, useLocation } from 'react-router';
import type { NavItem } from './document';
import { choice, responsiveSelect, select, text, variants, type Props } from './kit';
import { createRegistry, type ComponentDefinition, type ComponentRenderProps } from './registry';
import { useRenderMode } from './renderMode';
import { CONTENT_COMPONENTS, IconGlyph } from './contentComponents';
import { mediaUrl } from './data';
import { INTERACTIVE_COMPONENTS } from './interactiveComponents';
import { MEDIA_COMPONENTS } from './mediaComponents';
import { NAV_COMPONENTS } from './navComponents';
import { Collection, FIELD_TYPES, Form, FormField, Modal, RichText, runAction } from './dataComponents';
import { SiteLink, useSite } from './site';
import { PageStateContext } from './state';

/**
 * The built-in components.
 *
 * Every option a prop offers maps to a fixed class, never to a value copied into a style:
 * `gap: "md"` becomes `dcms-gap-md`, and the class reads the theme token. That keeps every
 * look inside the design kit, and keeps a value that did not come through the editor — a
 * hand-edited file, a model's tool call — from reaching CSS at all: an unknown value falls
 * back to the default (`choice`).
 */

const WIDTHS = ['narrow', 'normal', 'wide', 'full'] as const;
const SPACES = ['none', 'xs', 'sm', 'md', 'lg', 'xl'] as const;
const ALIGNS = ['start', 'center', 'end'] as const;

// Human labels for each option list — what the inspector shows instead of the stored value.
const WIDTH_LABELS = { narrow: 'Narrow', normal: 'Normal', wide: 'Wide', full: 'Full width' } as const;
const SPACE_LABELS = { none: 'None', xs: 'Extra small', sm: 'Small', md: 'Medium', lg: 'Large', xl: 'Extra large' } as const;
const ALIGN_LABELS = { start: 'Left', center: 'Centre', end: 'Right' } as const;

// ---------------------------------------------------------------------------

function Page({ slot }: ComponentRenderProps) {
  return <main className="dcms-page">{slot('default')}</main>;
}

const BACKGROUNDS = ['none', 'alt', 'soft', 'inverse'] as const;
const OVERLAYS = ['dark', 'light', 'none'] as const;
const SECTION_SPACES = ['none', 'sm', 'md', 'lg'] as const;

/** A section's anchor as an element id: lower-case letters, digits and dashes, or none. */
export function anchorId(value: unknown): string | undefined {
  const id = typeof value === 'string' ? value.trim().toLowerCase().replace(/[^a-z0-9-]+/g, '-').replace(/^-+|-+$/g, '') : '';
  return id || undefined;
}

function Section({ props, responsive, slot }: ComponentRenderProps<Props>) {
  const v = variants(props, responsive);
  const bg = choice(props.background, BACKGROUNDS, 'none');
  const py = v('spacing', SECTION_SPACES, 'md', (x) => `dcms-py-${x}`);
  const width = v('width', WIDTHS, 'normal', (x) => `dcms-width-${x}`);
  // A background picture, behind a tint so text stays readable. The address goes through
  // mediaUrl (site paths, http(s), asset ids) before it reaches the style.
  const image = mediaUrl(props.image);
  const overlay = choice(props.overlay, OVERLAYS, 'dark');
  return (
    <section
      id={anchorId(props.anchor)}
      className={`dcms-section dcms-bg-${bg} ${py}${image ? ` dcms-section-image dcms-overlay-${overlay}` : ''}`}
      style={image ? { backgroundImage: `url("${encodeURI(image)}")` } : undefined}
    >
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
  const mode = useRenderMode();
  const pageState = useContext(PageStateContext);
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
  // Scroll, popup and toast actions run on click — on the site, never on the canvas.
  const onClick = mode === 'live' && action ? () => runAction(action, pageState) : undefined;
  return (
    <button type="button" className={className} onClick={onClick}>
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

function NavList({ items, current, onFollow }: { items: readonly NavItem[]; current: string | null; onFollow?: () => void }) {
  return (
    <ul className="dcms-nav-list">
      {items.map((item, i) => (
        <li key={`${i}:${item.to}`}>
          <SiteLink to={item.to} className="dcms-nav-link" aria-current={current !== null && item.to === current ? 'page' : undefined} onClick={onFollow}>
            {item.label}
          </SiteLink>
          {item.children?.length ? <NavList items={item.children} current={current} onFollow={onFollow} /> : null}
        </li>
      ))}
    </ul>
  );
}

function Navigation(props: ComponentRenderProps<Props>) {
  // Reading the router's location is only possible inside the router, so that half is split off.
  return useInRouterContext() ? <RoutedNavigation {...props} /> : <NavigationView {...props} current={null} />;
}

function RoutedNavigation(props: ComponentRenderProps<Props>) {
  return <NavigationView {...props} current={useLocation().pathname} />;
}

function NavigationView({ nodeId, props, current }: ComponentRenderProps<Props> & { current: string | null }) {
  const { app } = useSite();
  const mode = useRenderMode();
  const [open, setOpen] = useState(false);
  const menu = text(props.menu, 'main');
  const layout = choice(props.layout, NAV_LAYOUTS, 'horizontal');
  const collapse = layout === 'horizontal' && choice(props.mobile, ['menu', 'wrap'] as const, 'menu') === 'menu';
  const items = app?.navigation?.[menu] ?? [];
  if (!items.length) return mode === 'edit' ? <div className="dcms-outlet-placeholder">Menu “{menu}” is empty</div> : null;
  const listId = `dcms-nav-${nodeId}`;
  return (
    <nav className={`dcms-nav dcms-nav-${layout}${collapse ? ' dcms-nav-collapsible' : ''}${open ? ' dcms-nav-open' : ''}`} aria-label={menu}>
      {collapse && (
        <button
          type="button"
          className="dcms-nav-toggle"
          aria-expanded={open}
          aria-controls={listId}
          onClick={() => mode === 'live' && setOpen(!open)}
        >
          <IconGlyph name={open ? 'x' : 'menu'} />
          <span>{text(props.menuLabel, 'Menu')}</span>
        </button>
      )}
      <div id={listId} className="dcms-nav-panel">
        <NavList items={items} current={current} onFollow={() => setOpen(false)} />
      </div>
    </nav>
  );
}
// ---------------------------------------------------------------------------

const alignProp = responsiveSelect('align', 'Alignment', ALIGNS, 'start', ALIGN_LABELS, {
  group: 'style',
  description: 'Which side the text lines up with.',
});

export const BUILTIN_COMPONENTS: readonly ComponentDefinition[] = [
  {
    type: 'dcms.page',
    version: 1,
    label: 'Page',
    description: 'The page itself. Everything on it goes inside.',
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
    keywords: ['menu', 'links', 'header', 'navbar'],
    component: Navigation,
    props: [
      {
        kind: 'text',
        name: 'menu',
        label: 'Menu',
        default: 'main',
        maxLength: 64,
        group: 'content',
        description: 'Which of the site’s menus to show — “main” unless you made others in Pages.',
      },
      select('layout', 'Layout', NAV_LAYOUTS, 'horizontal', { horizontal: 'In a row', vertical: 'In a column' }, {
        group: 'layout',
        description: 'Links side by side, as in a header, or one under another, as in a footer or sidebar.',
      }),
      select('mobile', 'On phones', ['menu', 'wrap'] as const, 'menu', { menu: 'Behind a menu button', wrap: 'Links wrap onto lines' }, {
        group: 'behaviour',
        description: 'A row of links rarely fits a phone: fold it behind a menu button, or let it wrap.',
        showIf: { prop: 'layout', is: ['horizontal'] },
      }),
      {
        kind: 'text',
        name: 'menuLabel',
        label: 'Menu button label',
        default: 'Menu',
        maxLength: 30,
        group: 'content',
        description: 'The word next to the menu button on phones.',
        showIf: { prop: 'mobile', is: ['menu'] },
      },
    ],
  },
  {
    type: 'dcms.section',
    version: 1,
    label: 'Section',
    description: 'A full-width band of the page with its own background and spacing. Pages are built from sections stacked one under another.',
    category: 'Layout',
    keywords: ['band', 'block', 'area', 'row', 'background'],
    component: Section,
    props: [
      select('background', 'Background', BACKGROUNDS, 'none', { none: 'Page background', alt: 'Subtle', soft: 'Soft tint', inverse: 'Dark' }, {
        group: 'style',
        description: 'Alternate neighbouring sections between backgrounds and the page reads in clear bands.',
      }),
      responsiveSelect('spacing', 'Vertical spacing', SECTION_SPACES, 'md', { none: 'None', sm: 'Small', md: 'Medium', lg: 'Large' }, {
        group: 'layout',
        description: 'Room above and below the content.',
      }),
      responsiveSelect('width', 'Content width', WIDTHS, 'normal', WIDTH_LABELS, {
        group: 'layout',
        description: 'How wide the content may grow. The background always spans the whole page.',
      }),
      {
        kind: 'media',
        name: 'image',
        label: 'Background picture',
        group: 'style',
        description: 'A photo behind the whole band — for a hero. Text on it is kept readable by the tint below.',
      },
      select('overlay', 'Tint over the picture', OVERLAYS, 'dark', { dark: 'Darken (light text)', light: 'Lighten (dark text)', none: 'None' }, {
        group: 'style',
        description: 'Keeps text readable on a busy photo.',
      }),
      {
        kind: 'text',
        name: 'anchor',
        label: 'Anchor name',
        maxLength: 48,
        group: 'behaviour',
        description: 'A short name (e.g. “pricing”) so menus and buttons can link straight to this section: /#pricing.',
      },
    ],
    slots: [{ name: 'default', label: 'Content' }],
  },
  {
    type: 'dcms.container',
    version: 1,
    label: 'Container',
    description: 'Keeps its content at a comfortable reading width, centred on the page.',
    category: 'Layout',
    keywords: ['wrapper', 'width', 'centre', 'center'],
    component: Container,
    props: [
      responsiveSelect('width', 'Width', WIDTHS, 'normal', WIDTH_LABELS, {
        group: 'layout',
        description: 'How wide the content may grow.',
      }),
    ],
    slots: [{ name: 'default', label: 'Content' }],
  },
  {
    type: 'dcms.stack',
    version: 1,
    label: 'Stack',
    description: 'Puts things one under another, or side by side in a row, with even space between them.',
    category: 'Layout',
    keywords: ['row', 'column', 'flex', 'group', 'side by side'],
    component: Stack,
    props: [
      responsiveSelect('direction', 'Direction', DIRECTIONS, 'vertical', { vertical: 'One under another', horizontal: 'Side by side' }, {
        group: 'layout',
        description: 'Whether the items stack downwards or sit in a row. Rows often look best stacked on phones.',
      }),
      responsiveSelect('gap', 'Space between', SPACES, 'md', SPACE_LABELS, {
        group: 'layout',
        description: 'The gap between neighbouring items.',
      }),
      responsiveSelect('align', 'Align items', STACK_ALIGNS, 'stretch', { start: 'To the start', center: 'Centred', end: 'To the end', stretch: 'Fill the space' }, {
        group: 'layout',
        description: 'How items line up across the stack — left, centre or right in a column; top, middle or bottom in a row.',
      }),
      responsiveSelect('justify', 'Distribute', JUSTIFIES, 'start', { start: 'Packed at the start', center: 'Packed in the centre', end: 'Packed at the end', between: 'Spread out evenly' }, {
        group: 'layout',
        description: 'Where the items gather along the stack when there is room to spare.',
      }),
      {
        kind: 'boolean',
        name: 'wrap',
        label: 'Wrap onto new lines',
        default: false,
        group: 'layout',
        description: 'Let a row continue on the next line instead of squeezing its items.',
      },
    ],
    slots: [{ name: 'default', label: 'Items' }],
  },
  {
    type: 'dcms.grid',
    version: 1,
    label: 'Grid',
    description: 'Equal columns that wrap onto new rows — for cards, features or photos. Use fewer columns on tablet and mobile.',
    category: 'Layout',
    keywords: ['columns', 'cards', 'tiles', 'gallery layout'],
    component: Grid,
    props: [
      responsiveSelect('columns', 'Columns', COLUMNS, '3', { '1': '1 column', '2': '2 columns', '3': '3 columns', '4': '4 columns', '5': '5 columns', '6': '6 columns' }, {
        group: 'layout',
        description: 'How many items sit side by side before the next row starts.',
      }),
      responsiveSelect('gap', 'Space between', SPACES, 'md', SPACE_LABELS, {
        group: 'layout',
        description: 'The gap between items, across and down.',
      }),
    ],
    slots: [{ name: 'default', label: 'Items' }],
  },
  {
    type: 'dcms.split',
    version: 1,
    label: 'Split',
    description: 'Two columns side by side — text beside an image. Stacks on phones.',
    category: 'Layout',
    keywords: ['two columns', 'side by side', 'image and text', 'columns'],
    component: Split,
    props: [
      responsiveSelect('ratio', 'Columns', SPLITS, '1-1', { '1-1': 'Equal halves', '2-1': 'First column wider', '1-2': 'Second column wider', stacked: 'One under the other' }, {
        group: 'layout',
        description: 'How the width is shared between the two columns.',
      }),
    ],
    slots: [
      { name: 'start', label: 'First column' },
      { name: 'end', label: 'Second column' },
    ],
  },
  {
    type: 'dcms.spacer',
    version: 1,
    label: 'Spacer',
    description: 'Empty vertical space, for when two things need more room between them.',
    category: 'Layout',
    keywords: ['space', 'gap', 'margin', 'blank'],
    component: Spacer,
    props: [
      responsiveSelect('size', 'Size', SPACER_SIZES, 'md', { xs: 'Extra small', sm: 'Small', md: 'Medium', lg: 'Large', xl: 'Extra large' }, {
        group: 'layout',
        description: 'How much space it adds.',
      }),
    ],
  },
  {
    type: 'dcms.collection',
    version: 1,
    label: 'Collection',
    description: 'A list of content from one of your plugins — events, posts, products. Design one item; the site repeats it for each.',
    category: 'Data',
    keywords: ['list', 'repeat', 'events', 'posts', 'blog', 'products', 'content', 'loop'],
    component: Collection,
    props: [
      {
        kind: 'source',
        name: 'source',
        label: 'Content',
        required: true,
        group: 'data',
        description: 'Which plugin content (or connected API) the list shows.',
      },
      {
        kind: 'number',
        name: 'limit',
        label: 'How many',
        default: 6,
        min: 1,
        max: 50,
        step: 1,
        group: 'data',
        description: 'The most items shown at once.',
      },
      {
        kind: 'text',
        name: 'tag',
        label: 'Only items tagged',
        maxLength: 64,
        group: 'data',
        description: 'Show only items with this tag. Leave empty to show everything.',
      },
      select('layout', 'Layout', ['grid', 'list'] as const, 'grid', { grid: 'Grid of cards', list: 'One under another' }, {
        group: 'layout',
        description: 'How the items are arranged.',
      }),
      select('paging', 'More than fit', ['none', 'more', 'pages'] as const, 'none', { none: 'Show only “How many”', more: '“Load more” button', pages: 'Page numbers' }, {
        group: 'behaviour',
        description: 'What visitors can do when there are more items than “How many”.',
      }),
      {
        kind: 'text',
        name: 'moreLabel',
        label: '“Load more” label',
        maxLength: 40,
        showIf: { prop: 'paging', is: ['more'] },
        group: 'content',
        description: 'The text on the “Load more” button.',
      },
    ],
    slots: [
      { name: 'item', label: 'Each item' },
      { name: 'loading', label: 'While loading' },
      { name: 'empty', label: 'When empty' },
      { name: 'error', label: 'On error' },
    ],
  },
  {
    type: 'dcms.richtext',
    version: 1,
    label: 'Rich text',
    description: 'Formatted text with headings, lists and links — often a post’s body.',
    category: 'Content',
    keywords: ['article', 'body', 'html', 'formatted', 'paragraphs'],
    component: RichText,
    props: [
      {
        kind: 'richText',
        name: 'html',
        label: 'Content',
        default: '<p>Formatted text.</p>',
        group: 'content',
        description: 'The formatted text. Scripts and unsafe markup are removed automatically.',
      },
    ],
  },
  {
    type: 'dcms.form',
    version: 1,
    label: 'Form',
    description: 'Collects what visitors enter — a contact form, a sign-up — and sends it to one of your Forms plugin’s forms.',
    category: 'Forms',
    keywords: ['contact', 'inquiry', 'sign up', 'submit', 'input'],
    component: Form,
    props: [
      {
        kind: 'text',
        name: 'instance',
        label: 'Forms plugin',
        required: true,
        maxLength: 64,
        group: 'data',
        description: 'The Forms plugin instance that receives the submissions, e.g. “forms”.',
      },
      {
        kind: 'text',
        name: 'form',
        label: 'Form name',
        required: true,
        maxLength: 128,
        group: 'data',
        description: 'Which of that plugin’s forms this is — submissions arrive under this name.',
      },
      {
        kind: 'text',
        name: 'submitLabel',
        label: 'Button label',
        default: 'Send',
        maxLength: 60,
        group: 'content',
        description: 'The text on the send button.',
      },
      {
        kind: 'text',
        name: 'successMessage',
        label: 'Thank-you message',
        default: 'Thank you — your message was sent.',
        maxLength: 300,
        group: 'content',
        description: 'What visitors see once their message is on its way.',
      },
    ],
    slots: [{ name: 'fields', label: 'Fields' }],
    starter: {
      fields: [
        { type: 'dcms.field', props: { name: 'name', label: 'Your name', required: true } },
        { type: 'dcms.field', props: { name: 'email', label: 'Email', type: 'email', required: true } },
        { type: 'dcms.field', props: { name: 'message', label: 'Message', type: 'textarea' } },
      ],
    },
  },
  {
    type: 'dcms.field',
    version: 1,
    label: 'Form field',
    description: 'One question in a form — a name, an email, a message, a choice from a list.',
    category: 'Forms',
    keywords: ['input', 'question', 'textbox', 'email', 'checkbox', 'dropdown', 'select', 'radio', 'date'],
    component: FormField,
    props: [
      {
        kind: 'text',
        name: 'label',
        label: 'Label',
        default: 'Name',
        maxLength: 120,
        group: 'content',
        description: 'The question visitors see above the field.',
      },
      select('type', 'Type', FIELD_TYPES, 'text', {
        text: 'Short text',
        email: 'Email address',
        tel: 'Phone number',
        number: 'Number',
        textarea: 'Long text',
        date: 'Date',
        select: 'Dropdown',
        radio: 'One of several',
        checkboxes: 'Any of several',
        checkbox: 'Yes / no checkbox',
      }, {
        group: 'content',
        description: 'What kind of answer it takes. Email and phone fields bring the right keyboard on phones.',
      }),
      {
        kind: 'text',
        name: 'options',
        label: 'Options',
        default: 'First option\nSecond option\nThird option',
        multiline: true,
        maxLength: 4000,
        showIf: { prop: 'type', is: ['select', 'radio', 'checkboxes'] },
        group: 'content',
        description: 'The choices, one per line.',
      },
      {
        kind: 'text',
        name: 'placeholder',
        label: 'Placeholder',
        maxLength: 120,
        showIf: { prop: 'type', is: ['text', 'email', 'tel', 'number', 'textarea', 'select'] },
        group: 'content',
        description: 'A greyed-out example shown inside the field until visitors type.',
      },
      {
        kind: 'text',
        name: 'help',
        label: 'Help text',
        maxLength: 300,
        group: 'content',
        description: 'A line under the field that explains what to enter or why you ask.',
      },
      {
        kind: 'boolean',
        name: 'required',
        label: 'Required',
        default: false,
        group: 'behaviour',
        description: 'The form will not send until this field is filled in.',
      },
      {
        kind: 'number',
        name: 'minLength',
        label: 'At least (characters)',
        min: 0,
        max: 10000,
        step: 1,
        showIf: { prop: 'type', is: ['text', 'textarea'] },
        group: 'behaviour',
        description: 'The shortest answer accepted.',
      },
      {
        kind: 'number',
        name: 'maxLength',
        label: 'At most (characters)',
        min: 1,
        max: 10000,
        step: 1,
        showIf: { prop: 'type', is: ['text', 'textarea'] },
        group: 'behaviour',
        description: 'The longest answer accepted.',
      },
      {
        kind: 'text',
        name: 'name',
        label: 'Field name',
        default: 'name',
        required: true,
        maxLength: 64,
        group: 'data',
        description: 'The name the answer is stored under in submissions. Keep it unique within the form.',
      },
    ],
  },
  {
    type: 'dcms.modal',
    version: 1,
    label: 'Popup',
    description: 'A window that stays hidden until a button opens it — for details, a form or a video.',
    category: 'Layout',
    keywords: ['modal', 'dialog', 'overlay', 'lightbox', 'window'],
    component: Modal,
    props: [
      {
        kind: 'text',
        name: 'title',
        label: 'Title (for screen readers)',
        default: 'Popup',
        maxLength: 120,
        group: 'content',
        description: 'Read out by screen readers when the popup opens. It is not shown on the page.',
      },
    ],
    slots: [{ name: 'default', label: 'Content' }],
  },
  {
    type: 'dcms.heading',
    version: 1,
    label: 'Heading',
    description: 'A title for the page or one of its sections.',
    category: 'Content',
    keywords: ['title', 'headline', 'h1', 'h2', 'subtitle'],
    component: Heading,
    props: [
      {
        kind: 'text',
        name: 'text',
        label: 'Text',
        default: 'Heading',
        maxLength: 300,
        group: 'content',
        description: 'The words of the heading.',
      },
      select('level', 'Level', LEVELS, '2', {
        '1': 'Heading 1 — page title',
        '2': 'Heading 2 — section',
        '3': 'Heading 3 — sub-section',
        '4': 'Heading 4',
        '5': 'Heading 5',
        '6': 'Heading 6',
      }, {
        group: 'style',
        description: 'How important the heading is. Use one Heading 1 per page; search engines and screen readers rely on the order.',
      }),
      alignProp,
    ],
  },
  {
    type: 'dcms.text',
    version: 1,
    label: 'Text',
    description: 'A paragraph of plain text.',
    category: 'Content',
    keywords: ['paragraph', 'copy', 'words', 'body', 'description'],
    component: Text,
    props: [
      {
        kind: 'text',
        name: 'text',
        label: 'Text',
        default: 'Write something here.',
        multiline: true,
        maxLength: 5000,
        group: 'content',
        description: 'The words of the paragraph.',
      },
      responsiveSelect('size', 'Size', SIZES, 'base', { sm: 'Small', base: 'Normal', lg: 'Large' }, {
        group: 'style',
        description: 'How big the letters are.',
      }),
      select('tone', 'Tone', TONES, 'default', { default: 'Normal', muted: 'Muted' }, {
        group: 'style',
        description: 'Muted text is lighter — for notes, captions and small print.',
      }),
      alignProp,
    ],
  },
  {
    type: 'dcms.image',
    version: 1,
    label: 'Image',
    description: 'A photo or picture from your media library.',
    category: 'Media',
    keywords: ['photo', 'picture', 'img', 'graphic'],
    component: Image,
    props: [
      { kind: 'media', name: 'src', label: 'Image', group: 'content', description: 'The picture to show, from your media library.' },
      {
        kind: 'text',
        name: 'alt',
        label: 'Description (alt text)',
        maxLength: 300,
        group: 'content',
        description: 'Say what the picture shows. Screen readers read it out and search engines index it.',
      },
      responsiveSelect('ratio', 'Aspect ratio', RATIOS, 'auto', { auto: 'As uploaded', '16-9': 'Widescreen (16:9)', '4-3': 'Classic (4:3)', '1-1': 'Square', '3-4': 'Portrait (3:4)' }, {
        group: 'style',
        description: 'The shape of the frame. Pictures of different sizes look tidy in a row when they share one.',
      }),
      select('fit', 'Fit', FITS, 'cover', { cover: 'Fill the frame (may crop)', contain: 'Show all of it' }, {
        group: 'style',
        description: 'What happens when the picture’s shape differs from the frame’s.',
      }),
      select('radius', 'Corners', RADII, 'none', { none: 'Square', sm: 'Slightly rounded', lg: 'Rounded' }, {
        group: 'style',
        description: 'How rounded the corners are.',
      }),
    ],
  },
  {
    type: 'dcms.button',
    version: 1,
    label: 'Button',
    description: 'A clickable button that opens a page, a link or a popup, or changes something on the page.',
    category: 'Content',
    keywords: ['link', 'call to action', 'cta', 'click'],
    component: Button,
    actions: ['navigate', 'open-external', 'scroll-to', 'open-modal', 'show-toast', 'set-state', 'toggle-state'],
    props: [
      {
        kind: 'text',
        name: 'label',
        label: 'Label',
        default: 'Button',
        maxLength: 80,
        group: 'content',
        description: 'The words on the button. Say what happens: “Book a table”, not “Click here”.',
      },
      select('variant', 'Style', VARIANTS, 'primary', { primary: 'Solid (main action)', secondary: 'Outline', ghost: 'Text only' }, {
        group: 'style',
        description: 'Use one solid button per section for the main action, and outline or text for the others.',
      }),
      select('size', 'Size', BUTTON_SIZES, 'md', { sm: 'Small', md: 'Medium', lg: 'Large' }, {
        group: 'style',
        description: 'How big the button is.',
      }),
    ],
  },
  ...CONTENT_COMPONENTS,
  ...MEDIA_COMPONENTS,
  ...INTERACTIVE_COMPONENTS,
  ...NAV_COMPONENTS,
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
