import { z } from 'zod';
import {
  GLOBAL_CSS,
  pageCssPath,
  pageHtmlPath,
  regionHtmlPath,
  slugFromRoutePath,
  slugify,
  uniqueSlug,
} from './paths';

/**
 * `site.json` — the Mode A manifest. It holds everything about a site that is not
 * page markup or CSS: the theme tokens, the page index, the navigation, the
 * shared regions and layouts, and the document-level settings. The pages
 * themselves live in `pages/*.html`, the regions in `regions/*.html` and their
 * rules in `styles/*.css`, so this file stays small and reviewable in a diff.
 *
 * Version 2 is the GrapesJS/file-map format. Version 1 was the component-tree
 * definition (`@dcms/editor-core`'s SiteDefinition) and is not readable here —
 * the literal below is what makes an old definition fail loudly instead of
 * loading as an empty site.
 */

export const SITE_MANIFEST_VERSION = 2;

/**
 * The design vocabulary a site is styled with.
 *
 * The groups are deliberately wider than colours and fonts. A look is not just a
 * palette — it is also how hard the shadows are, how tight the headings track,
 * how fast the type scale grows and how much air a section gets. Keeping all of
 * that as tokens is what lets a design kit be a plain data bundle: applying one
 * rewrites `styles/theme.css` and restyles the whole site, without the author
 * writing a line of CSS and without touching their own stylesheet.
 *
 * The rule for what belongs here: if the block stylesheet reads it as
 * `var(--dcms-…)`, it is a token. Anything else is `custom`.
 */
export const themeTokensSchema = z.object({
  /** Token name → CSS colour, emitted as `--dcms-color-<name>`. */
  colors: z.record(z.string(), z.string()).default({}),
  /** Token name → font stack, emitted as `--dcms-font-<name>`. */
  fonts: z.record(z.string(), z.string()).default({}),
  /** Token name → length, emitted as `--dcms-space-<name>`. */
  spacing: z.record(z.string(), z.string()).default({}),
  /** Step name → font size, emitted as `--dcms-text-<name>`. The type scale. */
  text: z.record(z.string(), z.string()).default({}),
  /** Step name → box-shadow, emitted as `--dcms-shadow-<name>`. */
  shadows: z.record(z.string(), z.string()).default({}),
  /**
   * The remaining named design decisions, emitted as `--dcms-<name>` with no
   * group infix: `container`, `radius-sm`, `radius-lg`, `radius-pill`,
   * `border-width`, `transition`, `leading-body`, `leading-heading`,
   * `weight-heading`, `tracking-heading`, `transform-heading`, `section-py`.
   *
   * One flat group rather than six single-purpose ones, because these share
   * nothing but being scalar — and a group per token would be a schema change
   * every time a kit wants one more knob.
   */
  metrics: z.record(z.string(), z.string()).default({}),
  /** Default corner radius, emitted as `--dcms-radius`. */
  radius: z.string().optional(),
  /** Escape hatch: raw custom properties merged into `:root` verbatim. */
  custom: z.record(z.string(), z.string()).default({}),
});

/** A theme that defines nothing — every group present and empty. */
export function emptyTheme(): ThemeTokens {
  return { colors: {}, fonts: {}, spacing: {}, text: {}, shadows: {}, metrics: {}, custom: {} };
}

export const seoMetaSchema = z.object({
  title: z.string(),
  description: z.string().optional(),
  ogImage: z.string().optional(),
  canonical: z.string().optional(),
  noIndex: z.boolean().optional(),
});

export const pageEntrySchema = z.object({
  id: z.string(),
  /** File-name stem: `pages/<slug>.html`, `styles/pages/<slug>.css`. */
  slug: z.string().regex(/^[a-z0-9][a-z0-9-]*$/, 'slug must be kebab-case'),
  /** Public route, used to derive the published file name. */
  path: z.string().regex(/^\//, 'page path must start with /'),
  title: z.string(),
  seo: seoMetaSchema,
  /** True for the page served at `/` when no other page claims it. */
  home: z.boolean().optional(),
  /**
   * Which layout wraps this page. Absent = the default layout; `''` = no chrome
   * at all, which is the only way to say "this page is the whole viewport".
   */
  layout: z.string().optional(),
});

/**
 * A band of markup shared by every page whose layout names it: a header, a
 * footer, a breadcrumb bar, a promo strip.
 *
 * It is a file (`regions/<slug>.html`) rather than a copy inside each page for
 * the obvious reason — a nav edited in eight places is a nav that disagrees with
 * itself — and it is a *region* rather than a fixed "header/footer" pair because
 * the useful set is not two. Where it lands is `placement`, which is the whole
 * positioning model: everything with `before` is emitted above the page body in
 * declaration order, everything with `after` below it.
 */
export const regionEntrySchema = z.object({
  id: z.string(),
  /** File-name stem: `regions/<slug>.html`. */
  slug: z.string().regex(/^[a-z0-9][a-z0-9-]*$/, 'slug must be kebab-case'),
  label: z.string(),
  placement: z.enum(['before', 'after']).default('before'),
});

/**
 * A named set of regions a page can adopt.
 *
 * Pages point at a layout instead of listing regions themselves, so "put the
 * announcement bar on every page" stays one edit. A page with no `layout` uses
 * the default layout; a page whose `layout` is the empty string opts out
 * entirely, which is what a landing page with no chrome wants.
 */
export const layoutEntrySchema = z.object({
  id: z.string(),
  label: z.string(),
  /** Region ids, in the order they are emitted within their placement. */
  regions: z.array(z.string()).default([]),
  /** Used by pages that name no layout. Exactly one layout should carry it. */
  default: z.boolean().optional(),
});

export interface NavItem {
  label: string;
  path: string;
  children?: NavItem[];
}

export const navItemSchema: z.ZodType<NavItem> = z.lazy(() =>
  z.object({
    label: z.string(),
    path: z.string(),
    children: z.array(navItemSchema).optional(),
  }),
);

export const siteSettingsSchema = z.object({
  /** `<html lang>` for every page. */
  lang: z.string().default('en'),
  favicon: z.string().optional(),
  /** Raw markup appended to `<head>` — analytics snippets, verification tags. */
  headHtml: z.string().optional(),
  /** Raw markup appended before `</body>`. */
  bodyEndHtml: z.string().optional(),
  /** Render the manifest nav as a `<nav>` above each page's body. */
  renderNav: z.boolean().default(true),
});

export const siteManifestSchema = z.object({
  version: z.literal(SITE_MANIFEST_VERSION),
  theme: themeTokensSchema.default(emptyTheme()),
  pages: z.array(pageEntrySchema).min(1, 'a site needs at least one page'),
  nav: z.array(navItemSchema).default([]),
  /** Shared bands, each backed by `regions/<slug>.html`. */
  regions: z.array(regionEntrySchema).default([]),
  layouts: z.array(layoutEntrySchema).default([]),
  settings: siteSettingsSchema.default({ lang: 'en', renderNav: true }),
});

export type ThemeTokens = z.infer<typeof themeTokensSchema>;
export type SeoMeta = z.infer<typeof seoMetaSchema>;
export type PageEntry = z.infer<typeof pageEntrySchema>;
export type RegionEntry = z.infer<typeof regionEntrySchema>;
export type LayoutEntry = z.infer<typeof layoutEntrySchema>;
export type SiteSettings = z.infer<typeof siteSettingsSchema>;
export type SiteManifest = z.infer<typeof siteManifestSchema>;

export function parseSiteManifest(input: unknown): SiteManifest {
  return siteManifestSchema.parse(input);
}

/** Parse without throwing — the code view needs the errors, not an exception. */
export function safeParseSiteManifest(input: unknown) {
  return siteManifestSchema.safeParse(input);
}

export function serializeSiteManifest(manifest: SiteManifest): string {
  return `${JSON.stringify(manifest, null, 2)}\n`;
}

/** The page a request for `/` resolves to: the explicit home, else the first. */
export function homePage(manifest: SiteManifest): PageEntry {
  return manifest.pages.find((p) => p.home) ?? manifest.pages.find((p) => p.path === '/') ?? manifest.pages[0];
}

export function findPageBySlug(manifest: SiteManifest, slug: string): PageEntry | undefined {
  return manifest.pages.find((p) => p.slug === slug);
}

/** Every file this manifest expects to exist, for load-time validation. */
export function expectedFiles(manifest: SiteManifest): string[] {
  return [
    GLOBAL_CSS,
    ...manifest.pages.flatMap((p) => [pageHtmlPath(p.slug), pageCssPath(p.slug)]),
    ...manifest.regions.map((r) => regionHtmlPath(r.slug)),
  ];
}

/** The layout a page adopts, or null when it opted out or none is defined. */
export function layoutForPage(manifest: SiteManifest, page: PageEntry): LayoutEntry | null {
  // Explicit `''` is "no chrome", and is not the same as an absent value —
  // hence the undefined check rather than a falsy one.
  if (page.layout === '') return null;
  if (page.layout !== undefined) {
    return manifest.layouts.find((l) => l.id === page.layout) ?? defaultLayout(manifest);
  }
  return defaultLayout(manifest);
}

export function defaultLayout(manifest: SiteManifest): LayoutEntry | null {
  return manifest.layouts.find((l) => l.default) ?? manifest.layouts[0] ?? null;
}

/**
 * The regions wrapping a page, split by where they go.
 *
 * Resolution is the layout's order filtered through the region index, so a
 * layout naming a region that was since deleted quietly renders without it
 * rather than failing a publish over a dangling id.
 */
export function regionsForPage(
  manifest: SiteManifest,
  page: PageEntry,
): { before: RegionEntry[]; after: RegionEntry[] } {
  const layout = layoutForPage(manifest, page);
  if (!layout) return { before: [], after: [] };
  const byId = new Map(manifest.regions.map((r) => [r.id, r]));
  const chosen = layout.regions.map((id) => byId.get(id)).filter((r): r is RegionEntry => !!r);
  return {
    before: chosen.filter((r) => r.placement === 'before'),
    after: chosen.filter((r) => r.placement === 'after'),
  };
}

export function findRegionBySlug(manifest: SiteManifest, slug: string): RegionEntry | undefined {
  return manifest.regions.find((r) => r.slug === slug);
}

/** Build a region entry with a slug unique within the manifest. */
export function makeRegionEntry(
  manifest: SiteManifest,
  label: string,
  id: string,
  placement: RegionEntry['placement'] = 'before',
): RegionEntry {
  const slug = uniqueSlug(
    slugify(label),
    manifest.regions.map((r) => r.slug),
  );
  return { id, slug, label, placement };
}

/** Build a page entry for a new route, with a slug unique within the manifest. */
export function makePageEntry(
  manifest: SiteManifest,
  routePath: string,
  title: string,
  id: string,
): PageEntry {
  const slug = uniqueSlug(
    slugFromRoutePath(routePath),
    manifest.pages.map((p) => p.slug),
  );
  return { id, slug, path: routePath, title, seo: { title } };
}

/** A one-page manifest — what a brand-new Mode A site starts from. */
export function emptySiteManifest(title = 'Home'): SiteManifest {
  return {
    version: SITE_MANIFEST_VERSION,
    theme: emptyTheme(),
    pages: [{ id: 'home', slug: 'home', path: '/', title, seo: { title }, home: true }],
    nav: [],
    regions: [],
    layouts: [],
    settings: { lang: 'en', renderNav: true },
  };
}
