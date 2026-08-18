import {
  GLOBAL_CSS,
  SITE_JSON,
  THEME_CSS,
  pageCssPath,
  pageHtmlPath,
  renderThemeCss,
  safeParseSiteManifest,
  serializeSiteManifest,
  slugFromPageCssPath,
  slugFromPageHtmlPath,
  type PageEntry,
  type SiteManifest,
} from '@dcms/gjs-schema';

/**
 * The Mode A project as the builder sees it: a manifest plus, per page, its
 * markup and its rules. This module is the only place that knows how those map
 * onto file paths, so the storage bridge, the code view and the page panel all
 * agree on what lives where.
 */

export interface ProjectPage {
  entry: PageEntry;
  html: string;
  css: string;
}

export interface Project {
  manifest: SiteManifest;
  pages: ProjectPage[];
  globalCss: string;
  /** Files the builder does not own (committed assets, robots.txt, …). */
  extras: Record<string, string>;
}

export interface ProjectLoadResult {
  project: Project | null;
  /** Why the file map could not be read as a project, if it could not. */
  error: string | null;
}

/**
 * Read a file map into a project. A malformed `site.json` is reported rather
 * than thrown: the builder shows the error and offers the code view, which is
 * the only place the author can actually fix it.
 */
export function readProject(files: Record<string, string>): ProjectLoadResult {
  const raw = files[SITE_JSON];
  if (raw === undefined) {
    return { project: null, error: `${SITE_JSON} is missing.` };
  }

  let parsedJson: unknown;
  try {
    parsedJson = JSON.parse(raw);
  } catch (e) {
    return { project: null, error: `${SITE_JSON} is not valid JSON: ${(e as Error).message}` };
  }

  const result = safeParseSiteManifest(parsedJson);
  if (!result.success) {
    const first = result.error.issues[0];
    const where = first?.path.length ? ` at ${first.path.join('.')}` : '';
    return { project: null, error: `${SITE_JSON} is not a valid site manifest${where}: ${first?.message}` };
  }

  const manifest = result.data;
  const pages: ProjectPage[] = manifest.pages.map((entry) => ({
    entry,
    html: files[pageHtmlPath(entry.slug)] ?? '',
    css: files[pageCssPath(entry.slug)] ?? '',
  }));

  const owned = new Set<string>([SITE_JSON, THEME_CSS, GLOBAL_CSS]);
  for (const page of pages) {
    owned.add(pageHtmlPath(page.entry.slug));
    owned.add(pageCssPath(page.entry.slug));
  }
  const extras: Record<string, string> = {};
  for (const [path, content] of Object.entries(files)) {
    if (!owned.has(path)) extras[path] = content;
  }

  return {
    project: { manifest, pages, globalCss: files[GLOBAL_CSS] ?? '', extras },
    error: null,
  };
}

/**
 * The files a project maps to. `theme.css` is regenerated here rather than
 * carried through, which is what makes it a derived file the code view can show
 * read-only without ever losing an edit.
 */
export function projectFiles(project: Project): Record<string, string> {
  const files: Record<string, string> = {
    ...project.extras,
    [SITE_JSON]: serializeSiteManifest(project.manifest),
    [THEME_CSS]: renderThemeCss(project.manifest.theme),
    [GLOBAL_CSS]: project.globalCss,
  };
  for (const page of project.pages) {
    files[pageHtmlPath(page.entry.slug)] = page.html;
    files[pageCssPath(page.entry.slug)] = page.css;
  }
  return files;
}

/** Every stylesheet in the project, for the class/custom-property index. */
export function stylesheets(project: Project): Record<string, string> {
  const sheets: Record<string, string> = {
    [THEME_CSS]: renderThemeCss(project.manifest.theme),
    [GLOBAL_CSS]: project.globalCss,
  };
  for (const page of project.pages) {
    sheets[pageCssPath(page.entry.slug)] = page.css;
  }
  for (const [path, content] of Object.entries(project.extras)) {
    if (path.endsWith('.css')) sheets[path] = content;
  }
  return sheets;
}

/** Which page a project file belongs to, or null for a shared/unrelated file. */
export function pageSlugOf(path: string): string | null {
  return slugFromPageHtmlPath(path) ?? slugFromPageCssPath(path);
}

/** Paths the builder owns and rewrites on save (so the code view can flag them). */
export function isProjectFile(path: string): boolean {
  return (
    path === SITE_JSON ||
    path === THEME_CSS ||
    path === GLOBAL_CSS ||
    pageSlugOf(path) !== null
  );
}

/** The files a page occupies, for deletion when the page is removed. */
export function pageFiles(slug: string): string[] {
  return [pageHtmlPath(slug), pageCssPath(slug)];
}
