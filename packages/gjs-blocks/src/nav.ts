/**
 * Navigation that has to know which page it is on.
 *
 * Breadcrumbs and "you are here" highlighting are the two things a *shared*
 * region cannot express as markup: a header authored once and shown on forty
 * pages would otherwise carry one page's trail onto all of them. So the markup
 * declares intent (`data-dcms-nav`) and the trail is filled in from the current
 * path — in the canvas by this module, on the published page by the ES5 copy in
 * `hydrate.js`, which `runtimeParity.test.ts` keeps honest.
 */

/** Marks an element whose contents depend on the current page. */
export const NAV_ATTR = 'data-dcms-nav';

/** The label a breadcrumb trail starts with, overridable per element. */
export const HOME_LABEL_ATTR = 'data-home-label';

export interface RouteEntry {
  path: string;
  title: string;
}

export interface Crumb {
  path: string;
  label: string;
  /** True for the page being viewed, which is rendered as text, not a link. */
  current: boolean;
}

/** `/blog/hello-world` → Home / Blog / Hello world. */
export function breadcrumbTrail(
  path: string,
  routes: readonly RouteEntry[] = [],
  homeLabel = 'Home',
): Crumb[] {
  const clean = normalize(path);
  const crumbs: Crumb[] = [{ path: '/', label: titleFor('/', routes) ?? homeLabel, current: clean === '/' }];
  if (clean === '/') return crumbs;

  let walked = '';
  const segments = clean.split('/').filter(Boolean);
  segments.forEach((segment, index) => {
    walked += `/${segment}`;
    crumbs.push({
      path: walked,
      // An intermediate segment often has no page of its own (`/blog/post` on a
      // site with no `/blog`), so the slug is humanised rather than dropped —
      // a trail with a gap in it reads as a bug.
      label: titleFor(walked, routes) ?? humanize(segment),
      current: index === segments.length - 1,
    });
  });
  return crumbs;
}

/** Fill every navigation element under `root` for the page at `path`. */
export function applyNav(
  root: ParentNode & { ownerDocument?: Document | null },
  path: string,
  routes: readonly RouteEntry[] = [],
): void {
  const doc = root.ownerDocument ?? (root as unknown as Document);
  for (const el of Array.from(root.querySelectorAll<HTMLElement>(`[${NAV_ATTR}]`))) {
    const kind = el.getAttribute(NAV_ATTR);
    if (kind === 'breadcrumbs') fillBreadcrumbs(doc, el, path, routes);
    else if (kind === 'menu') markCurrent(el, path);
  }
}

function fillBreadcrumbs(
  doc: Document,
  el: HTMLElement,
  path: string,
  routes: readonly RouteEntry[],
): void {
  const list = el.querySelector('ol,ul') ?? el;
  const crumbs = breadcrumbTrail(path, routes, el.getAttribute(HOME_LABEL_ATTR) ?? 'Home');

  list.innerHTML = '';
  for (const crumb of crumbs) {
    const item = doc.createElement('li');
    if (crumb.current) {
      item.setAttribute('aria-current', 'page');
      item.textContent = crumb.label;
    } else {
      const link = doc.createElement('a');
      link.setAttribute('href', crumb.path);
      link.textContent = crumb.label;
      item.appendChild(link);
    }
    list.appendChild(item);
  }
}

function markCurrent(el: HTMLElement, path: string): void {
  const here = normalize(path);
  for (const link of Array.from(el.querySelectorAll<HTMLAnchorElement>('a[href]'))) {
    const href = link.getAttribute('href') ?? '';
    // Only same-site paths can be "here"; an absolute URL to another host never is.
    if (href.charAt(0) !== '/') continue;
    if (normalize(href) === here) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
}

/** `/blog/` and `/blog/index.html` are the same page as `/blog`. */
export function normalize(path: string): string {
  let clean = (path || '/').split('?')[0]!.split('#')[0]!;
  clean = clean.replace(/index\.html$/, '');
  if (clean.length > 1 && clean.endsWith('/')) clean = clean.slice(0, -1);
  return clean === '' ? '/' : clean;
}

function titleFor(path: string, routes: readonly RouteEntry[]): string | null {
  const found = routes.find((r) => normalize(r.path) === normalize(path));
  return found ? found.title : null;
}

function humanize(segment: string): string {
  const words = decodeURIComponent(segment).replace(/[-_]+/g, ' ').trim();
  return words.charAt(0).toUpperCase() + words.slice(1);
}
