/**
 * A site's say over search engines (#648), as the console edits it. The server keeps the same
 * rules (`SiteSearchSettings` in Dcms.Shared.Data): `/about` one page, a `:name` segment any one
 * segment (`/blog/:slug`, every post), a final `/*` a path and everything under it.
 */

export interface SearchSettings {
  indexable: boolean;
  exclude: string[];
  include: string[];
}

export interface SitemapPageInfo {
  path: string;
  instance?: string | null;
  contentType?: string | null;
}

export interface SearchView {
  settings: SearchSettings;
  /** Mode D lists its own pages (from its documents); every other site's sitemap is `include` alone. */
  automaticPages: boolean;
  /** Mode D: the last published build's pages; null before the first publish, and for other sites. */
  pages: SitemapPageInfo[] | null;
  sitemapUrl: string | null;
  environmentKey: string;
}

const segments = (path: string) => path.split('/').filter(Boolean);

/** Whether `rule` covers `path`, without case — the server's `SiteSearchSettings.Matches`. */
export function matches(rule: string, path: string): boolean {
  let r = segments(rule);
  const p = segments(path);
  const section = r.at(-1) === '*';
  if (section) {
    r = r.slice(0, -1);
    if (p.length < r.length) return false;
  } else if (p.length !== r.length) {
    return false;
  }
  return r.every((s, i) => s.startsWith(':') || s.toLowerCase() === p[i].toLowerCase());
}

/** The rule that hides a page, if any: the page's own first, so its switch can undo it. */
export function hiddenBy(settings: SearchSettings, path: string): string | undefined {
  return settings.exclude.find((r) => r.toLowerCase() === path.toLowerCase()) ?? settings.exclude.find((r) => matches(r, path));
}

/** A page's switch: off adds its own rule; on removes it (a broader rule may still hide it). */
export function setPageShown(settings: SearchSettings, path: string, shown: boolean): SearchSettings {
  const own = (r: string) => r.toLowerCase() === path.toLowerCase();
  return {
    ...settings,
    exclude: shown ? settings.exclude.filter((r) => !own(r)) : settings.exclude.some(own) ? settings.exclude : [...settings.exclude, path],
  };
}

/** What is wrong with a path typed into a list, or null; the server checks again. */
export function pathProblem(value: string, patterns: boolean): 'start' | 'chars' | 'pattern' | null {
  if (!value.startsWith('/')) return 'start';
  if (/[\s?#]/.test(value) || /\/\/|\/\.\.?(\/|$)/.test(value)) return 'chars';
  const parts = segments(value);
  const badStar = parts.some((s, i) => s.includes('*') && !(patterns && s === '*' && i === parts.length - 1));
  if (badStar || (!patterns && parts.some((s) => s.startsWith(':')))) return 'pattern';
  return null;
}
