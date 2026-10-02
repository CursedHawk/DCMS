import type { App, Page } from './document';

/**
 * A page's `<head>`: title, description, canonical link, Open Graph and robots.
 *
 * Computed once (`pageHead`) and used twice — applied to the live document on every navigation
 * (`applyHead`), and written into each route's HTML at build time (`headHtml`, used by the Vite
 * plugin in `vite.ts`). The second is what a crawler or a link preview sees, since neither runs
 * the app; the two cannot disagree because they are the same function.
 */

export interface HeadTags {
  title: string;
  /** `name` meta tags (description, robots) and `property` ones (og:*). */
  meta: { key: 'name' | 'property'; name: string; content: string }[];
  canonical?: string;
}

export function pageHead(app: App, page: Page): HeadTags {
  const title = page.seo?.title || page.title;
  const fullTitle = app.seo?.titleTemplate ? app.seo.titleTemplate.replace('%s', title) : title;
  const description = page.seo?.description ?? app.seo?.description;
  const image = page.seo?.ogImage ?? app.seo?.ogImage;

  const meta: HeadTags['meta'] = [];
  if (description) meta.push({ key: 'name', name: 'description', content: description });
  meta.push({ key: 'property', name: 'og:title', content: fullTitle });
  if (description) meta.push({ key: 'property', name: 'og:description', content: description });
  if (image) meta.push({ key: 'property', name: 'og:image', content: image });
  if (page.seo?.noIndex) meta.push({ key: 'name', name: 'robots', content: 'noindex' });
  return { title: fullTitle, meta, canonical: page.seo?.canonical };
}

const MANAGED = 'data-dcms-head';

/** Replace the tags the previous page set; leave everything else in the head alone. */
export function applyHead(doc: Document, app: App, page: Page): void {
  const head = pageHead(app, page);
  doc.title = head.title;
  for (const el of Array.from(doc.head.querySelectorAll(`[${MANAGED}]`))) el.remove();
  for (const tag of head.meta) {
    const el = doc.createElement('meta');
    el.setAttribute(tag.key, tag.name);
    el.setAttribute('content', tag.content);
    el.setAttribute(MANAGED, '');
    doc.head.appendChild(el);
  }
  if (head.canonical) {
    const link = doc.createElement('link');
    link.rel = 'canonical';
    link.href = head.canonical;
    link.setAttribute(MANAGED, '');
    doc.head.appendChild(link);
  }
}

function escapeHtml(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/** The same tags as markup, for the build to write into a route's HTML file. */
export function headHtml(head: HeadTags): string {
  const lines = [`<title>${escapeHtml(head.title)}</title>`];
  for (const tag of head.meta) {
    lines.push(`<meta ${tag.key}="${escapeHtml(tag.name)}" content="${escapeHtml(tag.content)}" ${MANAGED}>`);
  }
  if (head.canonical) lines.push(`<link rel="canonical" href="${escapeHtml(head.canonical)}" ${MANAGED}>`);
  return lines.join('\n    ');
}
