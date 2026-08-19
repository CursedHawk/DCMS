import type { ComponentCategory, DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Palette tile icons, as inline SVG.
 *
 * Inline rather than an icon-font or component import because BlockManager's
 * `media` is an HTML string rendered inside the block tile, and because these
 * strings are also what a headless (test) editor sees — an icon that needs React
 * would make the block registry untestable outside a browser.
 *
 * `currentColor` throughout, so tiles follow the admin theme in light and dark.
 */

const svg = (paths: string) =>
  `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" width="22" height="22">${paths}</svg>`;

const ICONS: Record<string, string> = {
  section: svg('<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 9h18"/>'),
  container: svg('<rect x="5" y="4" width="14" height="16" rx="2"/>'),
  row: svg('<rect x="3" y="6" width="18" height="12" rx="2"/><path d="M9 6v12M15 6v12"/>'),
  grid: svg('<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 9h18M3 15h18M9 3v18M15 3v18"/>'),
  stack: svg('<rect x="4" y="4" width="16" height="4" rx="1"/><rect x="4" y="10" width="16" height="4" rx="1"/><rect x="4" y="16" width="16" height="4" rx="1"/>'),
  spacer: svg('<path d="M4 6h16M4 18h16"/><path d="M12 9v6"/>'),
  divider: svg('<path d="M3 12h18"/>'),
  heading: svg('<path d="M6 4v16M18 4v16M6 12h12"/>'),
  text: svg('<path d="M4 6h16M4 12h16M4 18h10"/>'),
  quote: svg('<path d="M7 7h4v6H7z"/><path d="M13 7h4v6h-4z"/><path d="M7 13c0 2 1 3 3 4M13 13c0 2 1 3 3 4"/>'),
  list: svg('<path d="M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01"/>'),
  code: svg('<path d="M16 18l6-6-6-6M8 6l-6 6 6 6"/>'),
  badge: svg('<rect x="3" y="8" width="18" height="8" rx="4"/>'),
  image: svg('<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="8.5" cy="9.5" r="1.5"/><path d="M21 16l-5-5-6 6"/>'),
  gallery: svg('<rect x="3" y="3" width="8" height="8" rx="1"/><rect x="13" y="3" width="8" height="8" rx="1"/><rect x="3" y="13" width="8" height="8" rx="1"/><rect x="13" y="13" width="8" height="8" rx="1"/>'),
  video: svg('<rect x="2" y="5" width="14" height="14" rx="2"/><path d="M16 10l6-3v10l-6-3z"/>'),
  audio: svg('<path d="M9 18V5l10-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="16" cy="16" r="3"/>'),
  icon: svg('<path d="M12 2l3 6.5 7 .9-5 4.8 1.2 7L12 18l-6.2 3.2L7 14.2 2 9.4l7-.9z"/>'),
  navbar: svg('<rect x="3" y="5" width="18" height="5" rx="1"/><path d="M6 15h6M6 19h10"/>'),
  menu: svg('<path d="M4 6h16M4 12h16M4 18h16"/>'),
  breadcrumbs: svg('<path d="M3 12h4l3 4M10 8l-3 4M14 12h4M18 8l3 4-3 4"/>'),
  tabs: svg('<path d="M3 9h6V5h12v14H3z"/><path d="M9 5v4"/>'),
  accordion: svg('<rect x="3" y="4" width="18" height="5" rx="1"/><rect x="3" y="11" width="18" height="9" rx="1"/><path d="M17 6.5h.01"/>'),
  footer: svg('<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 15h18"/>'),
  pagination: svg('<rect x="2" y="9" width="5" height="6" rx="1"/><rect x="9.5" y="9" width="5" height="6" rx="1"/><rect x="17" y="9" width="5" height="6" rx="1"/>'),
  hero: svg('<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M7 10h10M7 14h6"/>'),
  features: svg('<rect x="3" y="4" width="7" height="7" rx="1"/><rect x="14" y="4" width="7" height="7" rx="1"/><rect x="3" y="13" width="7" height="7" rx="1"/><rect x="14" y="13" width="7" height="7" rx="1"/>'),
  cta: svg('<rect x="3" y="6" width="18" height="12" rx="2"/><rect x="8" y="13" width="8" height="3" rx="1.5"/>'),
  pricing: svg('<rect x="3" y="4" width="5" height="16" rx="1"/><rect x="9.5" y="2" width="5" height="20" rx="1"/><rect x="16" y="4" width="5" height="16" rx="1"/>'),
  testimonial: svg('<path d="M4 5h16v11H9l-5 4z"/><path d="M8 10h8"/>'),
  team: svg('<circle cx="8" cy="8" r="3"/><circle cx="17" cy="9" r="2.5"/><path d="M2.5 20a5.5 5.5 0 0111 0M14 20a4.5 4.5 0 017.5-3.3"/>'),
  stats: svg('<path d="M4 20V10M10 20V4M16 20v-7M22 20H2"/>'),
  logos: svg('<circle cx="5" cy="12" r="2.5"/><rect x="10" y="9.5" width="5" height="5" rx="1"/><path d="M19 9.5l2.5 5h-5z"/>'),
  faq: svg('<circle cx="12" cy="12" r="9"/><path d="M9.5 9.5a2.5 2.5 0 113.5 2.3V14"/><path d="M12 17.5h.01"/>'),
  timeline: svg('<path d="M6 3v18"/><circle cx="6" cy="8" r="2"/><circle cx="6" cy="16" r="2"/><path d="M10 8h10M10 16h7"/>'),
  steps: svg('<path d="M3 20h5v-5H3zM9.5 15h5v-5h-5zM16 10h5V5h-5z"/>'),
  newsletter: svg('<rect x="2" y="5" width="20" height="14" rx="2"/><path d="M2 8l10 6 10-6"/>'),
  contact: svg('<path d="M4 5h16v14H4z"/><path d="M8 9h8M8 13h5"/>'),
  map: svg('<path d="M9 3L3 6v15l6-3 6 3 6-3V3l-6 3z"/><path d="M9 3v15M15 6v15"/>'),
  button: svg('<rect x="3" y="8" width="18" height="8" rx="4"/>'),
  link: svg('<path d="M10 13a5 5 0 007.5.5l3-3a5 5 0 00-7-7l-1.5 1.5"/><path d="M14 11a5 5 0 00-7.5-.5l-3 3a5 5 0 007 7L12 19"/>'),
  form: svg('<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M7 9h10M7 13h10M7 17h4"/>'),
  input: svg('<rect x="3" y="8" width="18" height="8" rx="2"/><path d="M7 12h.01"/>'),
  select: svg('<rect x="3" y="8" width="18" height="8" rx="2"/><path d="M15 11l2 2 2-2"/>'),
  checkbox: svg('<rect x="4" y="4" width="16" height="16" rx="3"/><path d="M8.5 12.5l2.5 2.5 5-5"/>'),
  carousel: svg('<rect x="7" y="6" width="10" height="12" rx="2"/><path d="M4 9v6M20 9v6"/>'),
  modal: svg('<rect x="3" y="4" width="18" height="16" rx="2" opacity=".4"/><rect x="6" y="8" width="12" height="9" rx="2"/>'),
  countdown: svg('<circle cx="12" cy="13" r="8"/><path d="M12 9v4l2.5 2.5M9 2h6"/>'),
  social: svg('<circle cx="18" cy="5" r="3"/><circle cx="6" cy="12" r="3"/><circle cx="18" cy="19" r="3"/><path d="M8.6 10.6l6.8-4M8.6 13.4l6.8 4"/>'),
  progress: svg('<rect x="2" y="10" width="20" height="4" rx="2"/><rect x="2" y="10" width="11" height="4" rx="2" fill="currentColor" stroke="none"/>'),
  rating: svg('<path d="M12 3l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.4 6.8 19.1l1-5.8L3.5 9.2l5.9-.9z"/>'),
  cookie: svg('<path d="M12 3a9 9 0 109 9 4 4 0 01-4-4 4 4 0 01-5-5z"/><path d="M9 10h.01M13 14h.01M9.5 15h.01"/>'),
  embed: svg('<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M9 12l-2 2 2 2M15 12l2 2-2 2"/>'),
  raw: svg('<path d="M5 4h14v16H5z"/><path d="M9 9l-2 3 2 3M15 9l2 3-2 3"/>'),
  canvas: svg('<rect x="3" y="3" width="18" height="18" rx="2" stroke-dasharray="3 3"/><path d="M8 8h4v4H8z"/>'),
  plugin: svg('<path d="M9 3v4H5v6a4 4 0 004 4h6a4 4 0 004-4V7h-4V3"/><path d="M9 7h6"/>'),
  lightbox: svg('<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M9 12h6M12 9v6"/>'),
  sidebar: svg('<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M10 4v16"/>'),
  anchor: svg('<circle cx="12" cy="5" r="2"/><path d="M12 7v13M5 13a7 7 0 0014 0"/>'),
  logo: svg('<circle cx="12" cy="12" r="9"/><path d="M8 14l3-5 2 3 1.5-2 1.5 4z"/>'),
  tooltip: svg('<rect x="3" y="5" width="18" height="10" rx="2"/><path d="M10 15l2 3 2-3"/>'),
  comparison: svg('<rect x="3" y="4" width="8" height="16" rx="1"/><rect x="13" y="4" width="8" height="16" rx="1"/><path d="M3 9h8M13 9h8"/>'),
  article: svg('<rect x="4" y="3" width="16" height="18" rx="2"/><path d="M8 8h8M8 12h8M8 16h5"/>'),
  card: svg('<rect x="4" y="3" width="16" height="18" rx="2"/><rect x="4" y="3" width="16" height="7" rx="2"/><path d="M8 14h8M8 17h5"/>'),
  plan: svg('<rect x="5" y="3" width="14" height="18" rx="2"/><path d="M9 8h6M9 12h6M9 16h3"/>'),
  person: svg('<circle cx="12" cy="8" r="3.5"/><path d="M5 20a7 7 0 0114 0"/>'),
  stat: svg('<path d="M5 20V9M12 20V4M19 20v-8"/>'),
  step: svg('<circle cx="6" cy="12" r="3"/><path d="M11 12h9M16 8l4 4-4 4"/>'),
  eyebrow: svg('<path d="M4 7h7"/><path d="M4 12h16M4 17h11"/>'),
  slide: svg('<rect x="4" y="5" width="16" height="14" rx="2"/><path d="M4 15l4-4 3 3 4-4 5 5"/>'),
  download: svg('<path d="M12 3v12M8 11l4 4 4-4"/><path d="M4 19h16"/>'),
};

const CATEGORY_FALLBACK: Record<ComponentCategory, string> = {
  layout: 'section',
  typography: 'text',
  media: 'image',
  navigation: 'menu',
  section: 'hero',
  part: 'card',
  interactive: 'button',
  form: 'form',
  utility: 'raw',
  plugin: 'plugin',
  custom: 'card',
};

/** Look an icon up by name, falling back to the category's default tile. */
export function icon(name: string): string {
  return ICONS[name] ?? ICONS.section;
}

export function iconFor(spec: DcmsComponentSpec): string {
  return ICONS[spec.icon ?? ''] ?? ICONS[CATEGORY_FALLBACK[spec.category]] ?? ICONS.section;
}
