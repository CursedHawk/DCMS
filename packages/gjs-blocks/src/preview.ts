/**
 * What a data-bound component looks like once its content arrives.
 *
 * This is the same set of layouts the published runtime draws
 * (src/Services/Dcms.SiteBuilder/Runtime/hydrate.js) — cards, tiles, rows,
 * feature bands, article, media players, downloads — implemented here so the
 * canvas can show real tenant content instead of an empty grey box. An author
 * choosing "Tiles" should see tiles, and the preview should be wrong in no way
 * that matters.
 *
 * **hydrate.js and this module must change together.** They are deliberately two
 * implementations: the runtime ships as a dependency-free ES5 file to every
 * published site, while this runs in the admin bundle. The shared contract is the
 * placeholder (props + bindings) and the `dcms-*` class names below, and the
 * tests in preview.test.ts pin the class names precisely so a drift shows up
 * here rather than as a canvas that lies about the published page.
 *
 * Every class emitted here is styled by `BLOCKS_CSS` from theme tokens alone, so
 * a plugin block inherits the site's design kit exactly like a hand-built
 * section does — that is the whole reason the markup is this specific.
 */

export type PreviewLayout =
  | 'cards'
  | 'tiles'
  | 'list'
  | 'compact'
  | 'feature'
  | 'article'
  | 'video'
  | 'audio'
  | 'downloads';

/** How a collection of items is arranged. Only meaningful for cards and tiles. */
export type PreviewArrangement = 'grid' | 'masonry' | 'carousel';

export const PREVIEW_LAYOUTS: PreviewLayout[] = [
  'cards',
  'tiles',
  'list',
  'compact',
  'feature',
  'article',
  'video',
  'audio',
  'downloads',
];

/** One content item, in the shape both the admin and delivery APIs return. */
export interface PreviewItem {
  id?: string;
  slug?: string;
  data?: Record<string, unknown> | null;
  draft?: Record<string, unknown> | null;
  [key: string]: unknown;
}

export interface PreviewProps {
  heading?: string;
  subheading?: string;
  /** A "see everything" link beside the heading. */
  moreLabel?: string;
  moreHref?: string;
  layout?: string;
  arrangement?: string;
  /** Explicit column count for a grid; empty means fit as many as fit. */
  columns?: string | number;
  /** Passed through to `.dcms-card`'s `data-variant`. */
  cardVariant?: string;
  emptyText?: string;
  titleField?: string;
  bodyField?: string;
  imageField?: string;
  linkField?: string;
  metaField?: string;
  tagsField?: string;
  /** Trim the body text to roughly this many characters. 0 or absent = whole. */
  excerptLength?: string | number;
  /** Label for the per-item link in layouts that show one. */
  linkLabel?: string;
}

const TITLE_KEYS = ['title', 'name', 'heading', 'headline', 'label', 'artist'];
const BODY_KEYS = ['excerpt', 'summary', 'description', 'body', 'content', 'text', 'caption'];
const IMAGE_KEYS = ['coverImage', 'heroImage', 'image', 'images', 'cover', 'thumbnail', 'thumb', 'photo', 'poster', 'src'];
const MEDIA_KEYS = ['source', 'asset', 'url', 'src', 'file', 'audio', 'video', 'track', 'media', 'href'];
const LINK_KEYS = ['linkUrl', 'href', 'url', 'link'];
const META_KEYS = ['publishedAt', 'date', 'publishedOn', 'category', 'author', 'kind'];
const TAG_KEYS = ['tags', 'categories', 'keywords', 'labels'];

/**
 * The field-mapping value meaning "show nothing here".
 *
 * Empty already means "guess from the key name", so there was no way to say
 * *don't* — an author who did not want an excerpt had to hope no field was
 * conventionally named. This is the third state.
 */
export const FIELD_NONE = '-';

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function escapeHtml(value: unknown): string {
  return String(value ?? '').replace(/[&<>"']/g, (c) => {
    switch (c) {
      case '&':
        return '&amp;';
      case '<':
        return '&lt;';
      case '>':
        return '&gt;';
      case '"':
        return '&quot;';
      default:
        return '&#39;';
    }
  });
}

/** The fields of an item, whichever envelope it arrived in. */
export function fieldsOf(item: PreviewItem): Record<string, unknown> {
  return (item.data ?? item.draft ?? item) as Record<string, unknown>;
}

/**
 * Read a field by path, following dots.
 *
 * Tenant-defined fields do not sit beside the plugin's own: a content type that
 * declares `customFields` nests them under one key (`values.barva`), because the
 * plugin cannot know their names in advance. A flat lookup therefore found
 * exactly the fields every tenant shares and none of the ones that make a site
 * specific — which is most of the reason the field-mapping controls felt broken.
 *
 * The trailing search is the other half: an unqualified `barva` also finds
 * `values.barva`, so an author who typed the field name they see in the plugin's
 * own admin screen gets what they meant.
 */
export function readField(fields: Record<string, unknown>, path: string): unknown {
  if (!path) return undefined;
  if (path.includes('.')) {
    let current: unknown = fields;
    for (const key of path.split('.')) {
      if (current == null || typeof current !== 'object') return undefined;
      current = (current as Record<string, unknown>)[key];
    }
    return current;
  }

  const direct = fields[path];
  if (direct != null && direct !== '') return direct;

  // One level into any nested bag of values — the shape `customFields` produces.
  for (const value of Object.values(fields)) {
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      const nested = (value as Record<string, unknown>)[path];
      if (nested != null && nested !== '') return nested;
    }
  }
  return direct;
}

/**
 * The value for a slot: the author's explicit field mapping if they made one,
 * otherwise the first conventionally-named field that holds something. The
 * explicit mapping exists because the guess is only as good as the tenant's
 * naming, and a content type whose headline field is `nadpis` deserves to work.
 */
function slot(
  fields: Record<string, unknown>,
  explicit: string | undefined,
  fallbackKeys: string[],
): unknown {
  if (explicit === FIELD_NONE) return undefined;
  if (explicit) {
    const value = readField(fields, explicit);
    return value === '' ? undefined : value;
  }
  for (const key of fallbackKeys) {
    const value = readField(fields, key);
    if (value != null && value !== '') return value;
  }
  return undefined;
}

/** Resolve a media reference to a URL the browser can load. */
export function mediaUrl(ref: unknown): string {
  if (!ref) return '';
  if (Array.isArray(ref)) return ref.length ? mediaUrl(ref[0]) : '';
  if (typeof ref === 'object') {
    const record = ref as Record<string, unknown>;
    for (const key of MEDIA_KEYS) {
      if (record[key]) return mediaUrl(record[key]);
    }
    return mediaUrl(record.id ?? record.assetId ?? '');
  }
  const value = String(ref);
  return GUID.test(value) ? `/api/media/${value}/original` : value;
}

/**
 * A date rendered the way a reader expects rather than the way the API stored
 * it. Anything that is not a date is passed through untouched, because the meta
 * slot is just as often a category as a timestamp.
 */
export function formatMeta(value: unknown): string {
  if (value == null) return '';
  const text = String(value);
  if (!/^\d{4}-\d{2}-\d{2}/.test(text)) return text;
  const date = new Date(text);
  if (Number.isNaN(date.getTime())) return text;
  return date.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
}

/** Trim to roughly `max` characters, at a word boundary, with an ellipsis. */
export function truncate(text: string, max: number): string {
  if (!max || text.length <= max) return text;
  const cut = text.slice(0, max);
  const space = cut.lastIndexOf(' ');
  return `${(space > max * 0.6 ? cut.slice(0, space) : cut).replace(/[\s,;:.–—-]+$/, '')}…`;
}

function tagsOf(value: unknown): string[] {
  if (value == null) return [];
  const list = Array.isArray(value) ? value : String(value).split(/\s*[,;]\s*/);
  return list.map((t) => String(t).trim()).filter(Boolean).slice(0, 6);
}

interface Slots {
  title?: string;
  body?: string;
  image: string;
  link?: string;
  media: string;
  meta?: string;
  tags: string[];
}

function slotsOf(item: PreviewItem, props: PreviewProps): Slots {
  const fields = fieldsOf(item);
  const title = slot(fields, props.titleField, TITLE_KEYS);
  const body = slot(fields, props.bodyField, BODY_KEYS);
  const link = slot(fields, props.linkField, LINK_KEYS);
  const meta = slot(fields, props.metaField, META_KEYS);
  const limit = Number(props.excerptLength ?? 0) || 0;
  const bodyText = body == null ? undefined : truncate(String(body), limit);
  return {
    title: title == null ? undefined : String(title),
    body: bodyText,
    image: mediaUrl(slot(fields, props.imageField, IMAGE_KEYS)),
    link: link == null ? undefined : String(link),
    media: mediaUrl(slot(fields, undefined, MEDIA_KEYS)),
    meta: meta == null ? undefined : formatMeta(meta),
    tags: tagsOf(slot(fields, props.tagsField, TAG_KEYS)),
  };
}

function tagsHtml(tags: string[]): string {
  if (tags.length === 0) return '';
  return `<div class="dcms-tags">${tags
    .map((t) => `<span class="dcms-tag">${escapeHtml(t)}</span>`)
    .join('')}</div>`;
}

function linked(html: string, href: string | undefined, className: string): string {
  return href ? `<a class="${className}" href="${escapeHtml(href)}">${html}</a>` : html;
}

function cardHtml(item: PreviewItem, props: PreviewProps): string {
  const { title, body, image, link, meta, tags } = slotsOf(item, props);
  const variant = props.cardVariant || 'outline';
  const parts = [`<article class="dcms-card" data-variant="${escapeHtml(variant)}" data-hover="lift">`];
  if (image) {
    parts.push(
      `<img class="dcms-card-img" src="${escapeHtml(image)}" alt="${escapeHtml(title ?? '')}" loading="lazy" />`,
    );
  }
  parts.push('<div class="dcms-card-body">');
  if (meta) parts.push(`<p class="dcms-card-meta">${escapeHtml(meta)}</p>`);
  if (title) parts.push(`<h3 class="dcms-card-title">${escapeHtml(title)}</h3>`);
  if (body) parts.push(`<p class="dcms-card-text">${escapeHtml(body)}</p>`);
  parts.push(tagsHtml(tags));
  parts.push('</div></article>');
  return linked(parts.join(''), link, 'dcms-card-link');
}

/** An image tile with the title burned into the corner — for galleries. */
function tileHtml(item: PreviewItem, props: PreviewProps): string {
  const { title, image, link } = slotsOf(item, props);
  const inner =
    `<img src="${escapeHtml(image)}" alt="${escapeHtml(title ?? '')}" loading="lazy" />` +
    (title ? `<span class="dcms-tile-caption">${escapeHtml(title)}</span>` : '');
  return link
    ? `<a class="dcms-tile" href="${escapeHtml(link)}">${inner}</a>`
    : `<figure class="dcms-tile">${inner}</figure>`;
}

function rowHtml(item: PreviewItem, props: PreviewProps): string {
  const { title, body, link, meta } = slotsOf(item, props);
  const inner =
    (meta ? `<span class="dcms-row-meta">${escapeHtml(meta)}</span>` : '') +
    `<span class="dcms-row-title">${escapeHtml(title ?? '(untitled)')}</span>` +
    (body ? `<span class="dcms-row-text">${escapeHtml(body)}</span>` : '');
  return `<li class="dcms-row">${link ? `<a href="${escapeHtml(link)}">${inner}</a>` : inner}</li>`;
}

/** A row with a thumbnail — the shape a "latest posts" sidebar wants. */
function compactHtml(item: PreviewItem, props: PreviewProps): string {
  const { title, image, link, meta } = slotsOf(item, props);
  const text =
    `<span>${meta ? `<span class="dcms-row-meta">${escapeHtml(meta)}</span>` : ''}` +
    `<span class="dcms-row-title">${escapeHtml(title ?? '(untitled)')}</span></span>`;
  const inner =
    (image ? `<img src="${escapeHtml(image)}" alt="${escapeHtml(title ?? '')}" loading="lazy" />` : '') + text;
  return `<li class="dcms-row dcms-row-media">${link ? `<a href="${escapeHtml(link)}">${inner}</a>` : inner}</li>`;
}

/** A full-width band per item, image beside text. Alternates sides in CSS. */
function featureItemHtml(item: PreviewItem, props: PreviewProps): string {
  const { title, body, image, link, meta } = slotsOf(item, props);
  const out = ['<article class="dcms-feature-item">'];
  if (image) {
    out.push(`<img src="${escapeHtml(image)}" alt="${escapeHtml(title ?? '')}" loading="lazy" />`);
  }
  out.push('<div>');
  if (meta) out.push(`<span class="dcms-row-meta">${escapeHtml(meta)}</span>`);
  if (title) out.push(`<h3>${escapeHtml(title)}</h3>`);
  if (body) out.push(`<p>${escapeHtml(body)}</p>`);
  if (link) {
    out.push(
      `<a class="dcms-button" data-variant="link" href="${escapeHtml(link)}">${escapeHtml(
        props.linkLabel || 'Read more',
      )}</a>`,
    );
  }
  out.push('</div></article>');
  return out.join('');
}

function articleHtml(item: PreviewItem, props: PreviewProps): string {
  const { title, body, image, meta, tags } = slotsOf(item, props);
  const out = ['<article class="dcms-article">'];
  if (title) out.push(`<h1 class="dcms-article-title">${escapeHtml(title)}</h1>`);
  if (meta) out.push(`<p class="dcms-article-meta">${escapeHtml(meta)}</p>`);
  if (image) {
    out.push(`<img class="dcms-article-img" src="${escapeHtml(image)}" alt="${escapeHtml(title ?? '')}" />`);
  }
  if (body) out.push(`<div class="dcms-article-body">${escapeHtml(body)}</div>`);
  out.push(tagsHtml(tags));
  out.push('</article>');
  return out.join('');
}

function playerHtml(item: PreviewItem, props: PreviewProps, tag: 'video' | 'audio'): string {
  const { title, media, meta } = slotsOf(item, props);
  const out = ['<div class="dcms-card" data-variant="outline"><div class="dcms-card-body">'];
  if (title) out.push(`<h3 class="dcms-card-title">${escapeHtml(title)}</h3>`);
  if (meta) out.push(`<p class="dcms-card-meta">${escapeHtml(meta)}</p>`);
  if (media) {
    out.push(`<${tag} class="dcms-media" controls preload="metadata" src="${escapeHtml(media)}"></${tag}>`);
  }
  out.push('</div></div>');
  return out.join('');
}

function downloadHtml(item: PreviewItem, props: PreviewProps): string {
  const { title, media, meta } = slotsOf(item, props);
  const label = escapeHtml(title || media);
  const suffix = meta ? `<span class="dcms-row-meta">${escapeHtml(meta)}</span>` : '';
  return `<li class="dcms-download"><a href="${escapeHtml(media)}" download>${label}${suffix}</a></li>`;
}

/** The wrapper every multi-item layout shares, carrying columns and arrangement. */
function collection(items: PreviewItem[], props: PreviewProps, inner: (item: PreviewItem) => string): string {
  const attrs = [
    props.columns ? ` data-columns="${escapeHtml(props.columns)}"` : '',
    props.arrangement && props.arrangement !== 'grid'
      ? ` data-layout="${escapeHtml(props.arrangement)}"`
      : '',
  ].join('');
  return `<div class="dcms-collection"${attrs}>${items.map(inner).join('')}</div>`;
}

/** The layout to draw, honouring the author's choice over any guess. */
export function layoutOf(props: PreviewProps, fallback: PreviewLayout = 'cards'): PreviewLayout {
  const requested = props.layout;
  return PREVIEW_LAYOUTS.includes(requested as PreviewLayout) ? (requested as PreviewLayout) : fallback;
}

/** The body of a data-bound component: everything below its heading. */
export function previewBody(items: PreviewItem[], props: PreviewProps): string {
  if (items.length === 0) {
    return `<p class="dcms-empty">${escapeHtml(props.emptyText || 'No content published yet.')}</p>`;
  }
  switch (layoutOf(props)) {
    case 'article':
      return articleHtml(items[0]!, props);
    case 'tiles':
      return collection(items, props, (i) => tileHtml(i, props));
    case 'list':
      return `<ul class="dcms-list" data-style="none">${items.map((i) => rowHtml(i, props)).join('')}</ul>`;
    case 'compact':
      return `<ul class="dcms-list" data-style="none">${items.map((i) => compactHtml(i, props)).join('')}</ul>`;
    case 'feature':
      return items.map((i) => featureItemHtml(i, props)).join('');
    case 'video':
      return collection(items, props, (i) => playerHtml(i, props, 'video'));
    case 'audio':
      return collection(items, props, (i) => playerHtml(i, props, 'audio'));
    case 'downloads':
      return `<ul class="dcms-downloads">${items.map((i) => downloadHtml(i, props)).join('')}</ul>`;
    default:
      return collection(items, props, (i) => cardHtml(i, props));
  }
}

/**
 * The heading block: the title, an optional supporting line and the "see all"
 * link, laid out as one row so the link sits beside the heading rather than
 * orphaned under the last card.
 */
export function previewHead(props: PreviewProps): string {
  const heading = props.heading
    ? `<h2 class="dcms-heading">${escapeHtml(props.heading)}</h2>`
    : '';
  const sub = props.subheading
    ? `<p class="dcms-section-lead">${escapeHtml(props.subheading)}</p>`
    : '';
  const more =
    props.moreLabel && props.moreHref
      ? `<a class="dcms-more" href="${escapeHtml(props.moreHref)}">${escapeHtml(props.moreLabel)}</a>`
      : '';
  if (!heading && !sub && !more) return '';
  return `<div class="dcms-collection-head"><div>${heading}${sub}</div>${more}</div>`;
}

/** A whole data-bound component: its heading plus its body. */
export function previewHtml(items: PreviewItem[], props: PreviewProps): string {
  return previewHead(props) + previewBody(items, props);
}

/**
 * The canvas-only state for a component that cannot show anything yet — no
 * binding, an instance that no longer exists, content still loading. These have
 * no published counterpart: the point is to tell the author what to fix while
 * they are looking at it.
 */
export function previewNotice(kind: 'loading' | 'unbound' | 'missing' | 'error', detail = ''): string {
  const message = {
    loading: 'Loading content…',
    unbound: 'Choose a content source in the settings panel.',
    missing: `No enabled plugin instance called “${detail}”.`,
    error: 'Could not load this content.',
  }[kind];
  return `<p class="dcms-notice dcms-notice-${kind}">${escapeHtml(message)}</p>`;
}

/**
 * Canvas-only styling for the states above.
 *
 * Everything a plugin block *renders* is styled by `BLOCKS_CSS`, which the site
 * already loads in the canvas — duplicating it here would mean two stylesheets
 * disagreeing about what a card looks like, which is exactly the drift this
 * preview exists to avoid. What is left is the notice, which never reaches a
 * published page and therefore has nowhere else to live.
 */
export const PREVIEW_CSS = `
.dcms-notice{color:var(--dcms-color-muted,#6b7280);font-size:var(--dcms-text-sm,.9rem);padding:var(--dcms-space-md,1rem);margin:0;border:1px dashed var(--dcms-color-border-strong,#cbd5e1);border-radius:var(--dcms-radius,.5rem);text-align:center}
.dcms-notice-loading{opacity:.7}
`;
