/**
 * The Mode A repository layout. A Mode A site is stored as the same flat file map
 * (`{ files: { path: content } }`) the Mode B stack already speaks, so the git
 * repo, working drafts, granular autosave and the build webhook are shared with
 * no special-casing. These paths are the contract between the builder, the Monaco
 * code view and the C# site assembler.
 *
 *   site.json              manifest: theme, nav, pages, regions, layouts, settings
 *   pages/<slug>.html      one page's body markup
 *   regions/<slug>.html    a band shared by every page whose layout names it
 *   styles/theme.css       GENERATED from site.json.theme — never hand-edited
 *   styles/global.css      rules shared by every page
 *   styles/pages/<slug>.css  rules scoped to one page
 *   assets.json            Asset Manager entries (DCMS media library references)
 *   blocks/<name>.json     a tenant-authored component (see ./component)
 *   AGENTS.md              GENERATED authoring contract for AI agents
 */

export const SITE_JSON = 'site.json';
/** The generated authoring contract for AI agents (see @dcms/gjs-blocks' guide). */
export const AGENTS_MD = 'AGENTS.md';
export const ASSETS_JSON = 'assets.json';
export const THEME_CSS = 'styles/theme.css';
export const GLOBAL_CSS = 'styles/global.css';

export const PAGES_DIR = 'pages';
export const PAGE_CSS_DIR = 'styles/pages';
export const BLOCKS_DIR = 'blocks';
export const REGIONS_DIR = 'regions';

export function pageHtmlPath(slug: string): string {
  return `${PAGES_DIR}/${slug}.html`;
}

/**
 * A shared region's markup.
 *
 * Regions deliberately have no stylesheet of their own. A band that appears on
 * every page belongs in `styles/global.css`; a private sheet per region would
 * have to be linked from every page that uses it, and the one page that does not
 * would still pay for it — the opposite of the per-page cascade this format
 * exists to keep honest.
 */
export function regionHtmlPath(slug: string): string {
  return `${REGIONS_DIR}/${slug}.html`;
}

export function pageCssPath(slug: string): string {
  return `${PAGE_CSS_DIR}/${slug}.css`;
}

export function blockPath(name: string): string {
  return `${BLOCKS_DIR}/${name}.json`;
}

/** The page slug a `pages/<slug>.html` path refers to, or null if it isn't one. */
export function slugFromPageHtmlPath(path: string): string | null {
  const match = /^pages\/([^/]+)\.html$/.exec(path);
  return match ? match[1] : null;
}

/** The region slug a `regions/<slug>.html` path refers to, or null. */
export function slugFromRegionHtmlPath(path: string): string | null {
  const match = /^regions\/([^/]+)\.html$/.exec(path);
  return match ? match[1] : null;
}

/** The component name a `blocks/<name>.json` path refers to, or null. */
export function nameFromBlockPath(path: string): string | null {
  const match = /^blocks\/([^/]+)\.json$/.exec(path);
  return match ? match[1] : null;
}

/** The page slug a `styles/pages/<slug>.css` path refers to, or null. */
export function slugFromPageCssPath(path: string): string | null {
  const match = /^styles\/pages\/([^/]+)\.css$/.exec(path);
  return match ? match[1] : null;
}

/**
 * Files the builder regenerates from `site.json` on every save. The code view
 * shows them read-only: an edit here would be silently overwritten, which is
 * worse than not offering the edit at all.
 */
export function isGeneratedPath(path: string): boolean {
  return path === THEME_CSS || path === AGENTS_MD;
}

/** Every stylesheet a page pulls in, in cascade order (theme → global → page). */
export function stylesheetsForPage(slug: string): string[] {
  return [THEME_CSS, GLOBAL_CSS, pageCssPath(slug)];
}

const SLUG_STRIP = /[^a-z0-9]+/g;
// Unicode combining marks, written from a string so the range stays legible in source.
const COMBINING_MARKS = new RegExp('[\\u0300-\\u036f]', 'g');

/**
 * A filesystem- and URL-safe slug. Diacritics are folded rather than dropped so
 * a Czech page title still yields a readable file name.
 */
export function slugify(input: string): string {
  const folded = input.normalize('NFKD').replace(COMBINING_MARKS, '').toLowerCase();
  const slug = folded.replace(SLUG_STRIP, '-').replace(/^-+|-+$/g, '');
  return slug || 'page';
}

/** The slug for a route path: `/` → `home`, `/about/team` → `about-team`. */
export function slugFromRoutePath(routePath: string): string {
  const trimmed = routePath.replace(/^\/+|\/+$/g, '');
  return trimmed ? slugify(trimmed) : 'home';
}

/** Make `slug` unique against `taken` by appending -2, -3, … */
export function uniqueSlug(slug: string, taken: Iterable<string>): string {
  const used = new Set(taken);
  if (!used.has(slug)) return slug;
  for (let n = 2; ; n++) {
    const candidate = `${slug}-${n}`;
    if (!used.has(candidate)) return candidate;
  }
}

/**
 * What a `:param` route segment becomes in a published file name.
 *
 * `@` because a real segment is kebab-case (see `slugify`) and can never
 * contain one, so the mapping is unambiguous in both directions — the site host
 * has to be able to tell "the page for /events/anything" apart from "the page
 * for /events/at".
 */
export const WILDCARD_FILE_SEGMENT = '@';

/**
 * True for a route with a `:param` segment — a **detail route**.
 *
 * `/events/:slug` is one page that serves every event: the host resolves any
 * `/events/<something>` to it, and the detail component on it reads the last
 * URL segment to decide which item to fetch. Without this a card linking to
 * `/events/summer-party` fell through the host's SPA fallback and rendered the
 * home page at that address, which is the reason lists were not clickable.
 */
export function isDetailRoute(routePath: string): boolean {
  return routePath.split('/').some((segment) => segment.startsWith(':'));
}

/**
 * The published file name for a route path. Mirrors the existing Mode A
 * convention so links that worked before still work: `/` → index.html,
 * `/about/team` → about_team.html, `/events/:slug` → events_@.html.
 */
export function outputFileName(routePath: string): string {
  const trimmed = routePath.replace(/^\/+|\/+$/g, '');
  if (!trimmed) return 'index.html';
  const segments = trimmed
    .split('/')
    .map((segment) => (segment.startsWith(':') ? WILDCARD_FILE_SEGMENT : segment));
  return `${segments.join('_')}.html`;
}
