/**
 * What a data-bound component looks like once its content arrives.
 *
 * This is the same set of layouts the published runtime draws
 * (src/Services/Dcms.SiteBuilder/Runtime/hydrate.js) — cards, list, article,
 * media players, downloads — implemented here so the canvas can show real tenant
 * content instead of an empty grey box. An author choosing "Cards" should see
 * cards, and the preview should be wrong in no way that matters.
 *
 * **hydrate.js and this module must change together.** They are deliberately two
 * implementations: the runtime ships as a dependency-free ES5 file to every
 * published site, while this runs in the admin bundle. The shared contract is the
 * placeholder (props + bindings) and the `dcms-*` class names below, and the
 * tests in preview.test.ts pin the class names precisely so a drift shows up
 * here rather than as a canvas that lies about the published page.
 */

export type PreviewLayout = 'cards' | 'list' | 'article' | 'video' | 'audio' | 'downloads';

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
  layout?: string;
  emptyText?: string;
  titleField?: string;
  bodyField?: string;
  imageField?: string;
  linkField?: string;
}

const TITLE_KEYS = ['title', 'name', 'heading', 'headline', 'label', 'artist'];
const BODY_KEYS = ['excerpt', 'summary', 'description', 'body', 'content', 'text', 'caption'];
const IMAGE_KEYS = ['coverImage', 'heroImage', 'image', 'images', 'cover', 'thumbnail', 'thumb', 'photo', 'poster', 'src'];
const MEDIA_KEYS = ['source', 'asset', 'url', 'src', 'file', 'audio', 'video', 'track', 'media', 'href'];
const LINK_KEYS = ['linkUrl', 'href', 'url', 'link'];

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
  if (explicit) {
    const value = fields[explicit];
    return value === '' ? undefined : value;
  }
  for (const key of fallbackKeys) {
    const value = fields[key];
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

interface Slots {
  title?: string;
  body?: string;
  image: string;
  link?: string;
  media: string;
}

function slotsOf(item: PreviewItem, props: PreviewProps): Slots {
  const fields = fieldsOf(item);
  const title = slot(fields, props.titleField, TITLE_KEYS);
  const body = slot(fields, props.bodyField, BODY_KEYS);
  const link = slot(fields, props.linkField, LINK_KEYS);
  return {
    title: title == null ? undefined : String(title),
    body: body == null ? undefined : String(body),
    image: mediaUrl(slot(fields, props.imageField, IMAGE_KEYS)),
    link: link == null ? undefined : String(link),
    media: mediaUrl(slot(fields, undefined, MEDIA_KEYS)),
  };
}

function cardHtml(item: PreviewItem, props: PreviewProps): string {
  const { title, body, image, link } = slotsOf(item, props);
  const parts = ['<article class="dcms-card">'];
  if (image) {
    parts.push(
      `<img class="dcms-card-img" src="${escapeHtml(image)}" alt="${escapeHtml(title ?? '')}" loading="lazy" />`,
    );
  }
  parts.push('<div class="dcms-card-body">');
  if (title) parts.push(`<h3 class="dcms-card-title">${escapeHtml(title)}</h3>`);
  if (body) parts.push(`<p class="dcms-card-text">${escapeHtml(body)}</p>`);
  parts.push('</div></article>');
  const html = parts.join('');
  return link
    ? `<a href="${escapeHtml(link)}" style="text-decoration:none;color:inherit;">${html}</a>`
    : html;
}

function rowHtml(item: PreviewItem, props: PreviewProps): string {
  const { title, body, link } = slotsOf(item, props);
  const inner =
    `<span class="dcms-row-title">${escapeHtml(title ?? '(untitled)')}</span>` +
    (body ? `<span class="dcms-row-text">${escapeHtml(body)}</span>` : '');
  return `<li class="dcms-row">${link ? `<a href="${escapeHtml(link)}">${inner}</a>` : inner}</li>`;
}

function articleHtml(item: PreviewItem, props: PreviewProps): string {
  const { title, body, image } = slotsOf(item, props);
  const out = ['<article class="dcms-article">'];
  if (title) out.push(`<h1 class="dcms-article-title">${escapeHtml(title)}</h1>`);
  if (image) {
    out.push(`<img class="dcms-article-img" src="${escapeHtml(image)}" alt="${escapeHtml(title ?? '')}" />`);
  }
  if (body) out.push(`<div class="dcms-article-body">${escapeHtml(body)}</div>`);
  out.push('</article>');
  return out.join('');
}

function playerHtml(item: PreviewItem, props: PreviewProps, tag: 'video' | 'audio'): string {
  const { title, media } = slotsOf(item, props);
  const out = ['<div class="dcms-card"><div class="dcms-card-body">'];
  if (title) out.push(`<h3 class="dcms-card-title">${escapeHtml(title)}</h3>`);
  if (media) {
    out.push(`<${tag} class="dcms-media" controls preload="metadata" src="${escapeHtml(media)}"></${tag}>`);
  }
  out.push('</div></div>');
  return out.join('');
}

function downloadHtml(item: PreviewItem, props: PreviewProps): string {
  const { title, media } = slotsOf(item, props);
  return `<li class="dcms-download"><a href="${escapeHtml(media)}" download>${escapeHtml(title || media)}</a></li>`;
}

function collection(items: PreviewItem[], inner: (item: PreviewItem) => string): string {
  return `<div class="dcms-collection">${items.map(inner).join('')}</div>`;
}

/** The layout to draw, honouring the author's choice over any guess. */
export function layoutOf(props: PreviewProps, fallback: PreviewLayout = 'cards'): PreviewLayout {
  const requested = props.layout;
  const known: PreviewLayout[] = ['cards', 'list', 'article', 'video', 'audio', 'downloads'];
  return known.includes(requested as PreviewLayout) ? (requested as PreviewLayout) : fallback;
}

/** The body of a data-bound component: everything below its heading. */
export function previewBody(items: PreviewItem[], props: PreviewProps): string {
  if (items.length === 0) {
    return `<p class="dcms-empty">${escapeHtml(props.emptyText || 'No content published yet.')}</p>`;
  }
  switch (layoutOf(props)) {
    case 'article':
      return articleHtml(items[0]!, props);
    case 'list':
      return `<ul class="dcms-list">${items.map((i) => rowHtml(i, props)).join('')}</ul>`;
    case 'video':
      return collection(items, (i) => playerHtml(i, props, 'video'));
    case 'audio':
      return collection(items, (i) => playerHtml(i, props, 'audio'));
    case 'downloads':
      return `<ul class="dcms-downloads">${items.map((i) => downloadHtml(i, props)).join('')}</ul>`;
    default:
      return collection(items, (i) => cardHtml(i, props));
  }
}

/** A whole data-bound component: its heading plus its body. */
export function previewHtml(items: PreviewItem[], props: PreviewProps): string {
  const heading = props.heading
    ? `<h2 class="dcms-heading">${escapeHtml(props.heading)}</h2>`
    : '';
  return heading + previewBody(items, props);
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
 * Styling for the preview markup, injected into the canvas iframe. The published
 * page gets an equivalent sheet from hydrate.js; this exists so the canvas is not
 * a pile of unstyled text before the site's own CSS targets these classes.
 */
export const PREVIEW_CSS = `
.dcms-collection{display:grid;gap:1.25rem;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));width:100%}
.dcms-heading{margin:0 0 1rem;font:600 1.5rem/1.2 var(--dcms-font-heading,inherit)}
.dcms-card{display:flex;flex-direction:column;overflow:hidden;border:1px solid var(--dcms-color-border,#e5e7eb);border-radius:var(--dcms-radius,12px);background:var(--dcms-color-surface,#fff)}
.dcms-card-img{width:100%;aspect-ratio:16/9;object-fit:cover;display:block}
.dcms-card-body{padding:1rem;display:flex;flex-direction:column;gap:.5rem}
.dcms-card-title{margin:0;font:600 1.1rem/1.3 var(--dcms-font-heading,inherit)}
.dcms-card-text{margin:0;color:var(--dcms-color-muted,#6b7280);font-size:.95rem;line-height:1.5}
.dcms-list{list-style:none;margin:0;padding:0;display:flex;flex-direction:column;gap:.75rem}
.dcms-row{display:flex;flex-direction:column;gap:.25rem;padding-bottom:.75rem;border-bottom:1px solid var(--dcms-color-border,#e5e7eb)}
.dcms-row-title{font-weight:600}
.dcms-row-text{color:var(--dcms-color-muted,#6b7280);font-size:.95rem}
.dcms-article{max-width:48rem;margin:0 auto}
.dcms-article-title{font:700 2rem/1.2 var(--dcms-font-heading,inherit);margin:0 0 1rem}
.dcms-article-img{width:100%;border-radius:var(--dcms-radius,12px);margin:0 0 1rem;display:block}
.dcms-media{width:100%;border-radius:var(--dcms-radius,12px);display:block}
.dcms-downloads{list-style:none;margin:0;padding:0;display:flex;flex-direction:column;gap:.5rem}
.dcms-download a{display:inline-flex;align-items:center;gap:.5rem;color:var(--dcms-color-primary,#2563eb);text-decoration:none}
.dcms-empty,.dcms-notice{color:var(--dcms-color-muted,#6b7280);font-size:.95rem;padding:1rem;margin:0}
.dcms-notice{border:1px dashed var(--dcms-color-border,#cbd5e1);border-radius:var(--dcms-radius,12px);text-align:center}
`;
