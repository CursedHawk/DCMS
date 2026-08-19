import {
  GLOBAL_CSS,
  SITE_JSON,
  THEME_CSS,
  componentPath,
  nameFromBlockPath,
  pageCssPath,
  pageHtmlPath,
  regionHtmlPath,
  renderThemeCss,
  safeParseSiteManifest,
  safeParseComponentDefinition,
  serializeComponentDefinition,
  serializeSiteManifest,
  slugFromPageCssPath,
  slugFromPageHtmlPath,
  slugFromRegionHtmlPath,
  type ComponentDefinition,
  type PageEntry,
  type RegionEntry,
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

/**
 * A region is markup and nothing else. It has no stylesheet of its own — a band
 * on every page belongs in `global.css` — so unlike a page it is one file.
 */
export interface ProjectRegion {
  entry: RegionEntry;
  html: string;
}

export interface Project {
  manifest: SiteManifest;
  pages: ProjectPage[];
  regions: ProjectRegion[];
  /** The tenant's own components, from `blocks/*.json`. */
  components: ComponentDefinition[];
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
  const regions: ProjectRegion[] = manifest.regions.map((entry) => ({
    entry,
    html: files[regionHtmlPath(entry.slug)] ?? '',
  }));
  const components = readComponents(files);

  const owned = new Set<string>([SITE_JSON, THEME_CSS, GLOBAL_CSS]);
  for (const page of pages) {
    owned.add(pageHtmlPath(page.entry.slug));
    owned.add(pageCssPath(page.entry.slug));
  }
  for (const region of regions) owned.add(regionHtmlPath(region.entry.slug));
  for (const component of components) owned.add(componentPath(component.name));
  const extras: Record<string, string> = {};
  for (const [path, content] of Object.entries(files)) {
    if (!owned.has(path)) extras[path] = content;
  }

  return {
    project: { manifest, pages, regions, components, globalCss: files[GLOBAL_CSS] ?? '', extras },
    error: null,
  };
}

/**
 * The tenant's components, from every `blocks/*.json` the repo holds.
 *
 * A definition that does not parse is skipped rather than failing the load: the
 * files are editable in the code view, and half-typed JSON there should cost
 * that one block from the palette, not the ability to open the site. The code
 * view reports it as a diagnostic, which is where the author is looking.
 */
function readComponents(files: Record<string, string>): ComponentDefinition[] {
  const components: ComponentDefinition[] = [];
  for (const [path, content] of Object.entries(files)) {
    const name = nameFromBlockPath(path);
    if (!name) continue;
    try {
      const parsed = safeParseComponentDefinition(JSON.parse(content));
      if (parsed.success) components.push(parsed.data);
    } catch {
      /* not valid JSON — see above */
    }
  }
  return components.sort((a, b) => a.label.localeCompare(b.label));
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
  for (const region of project.regions) {
    files[regionHtmlPath(region.entry.slug)] = region.html;
  }
  for (const component of project.components) {
    files[componentPath(component.name)] = serializeComponentDefinition(component);
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
    pageSlugOf(path) !== null ||
    slugFromRegionHtmlPath(path) !== null ||
    nameFromBlockPath(path) !== null
  );
}

/** The files a page occupies, for deletion when the page is removed. */
export function pageFiles(slug: string): string[] {
  return [pageHtmlPath(slug), pageCssPath(slug)];
}

/** The component definitions a project holds, as specs the builder understands. */
export function componentOf(project: Project, name: string): ComponentDefinition | undefined {
  return project.components.find((c) => c.name === name);
}
