import { z } from 'zod';
import { GLOBAL_CSS, pageCssPath, pageHtmlPath, slugFromRoutePath, uniqueSlug } from './paths';

/**
 * `site.json` — the Mode A manifest. It holds everything about a site that is not
 * page markup or CSS: the theme tokens, the page index, the navigation and the
 * document-level settings. The pages themselves live in `pages/*.html` and their
 * rules in `styles/*.css`, so this file stays small and reviewable in a diff.
 *
 * Version 2 is the GrapesJS/file-map format. Version 1 was the component-tree
 * definition (`@dcms/editor-core`'s SiteDefinition) and is not readable here —
 * the literal below is what makes an old definition fail loudly instead of
 * loading as an empty site.
 */

export const SITE_MANIFEST_VERSION = 2;

export const themeTokensSchema = z.object({
  /** Token name → CSS colour, emitted as `--dcms-color-<name>`. */
  colors: z.record(z.string(), z.string()).default({}),
  /** Token name → font stack, emitted as `--dcms-font-<name>`. */
  fonts: z.record(z.string(), z.string()).default({}),
  /** Token name → length, emitted as `--dcms-space-<name>`. */
  spacing: z.record(z.string(), z.string()).default({}),
  /** Default corner radius, emitted as `--dcms-radius`. */
  radius: z.string().optional(),
  /** Escape hatch: raw custom properties merged into `:root` verbatim. */
  custom: z.record(z.string(), z.string()).default({}),
});

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
  theme: themeTokensSchema.default({ colors: {}, fonts: {}, spacing: {}, custom: {} }),
  pages: z.array(pageEntrySchema).min(1, 'a site needs at least one page'),
  nav: z.array(navItemSchema).default([]),
  settings: siteSettingsSchema.default({ lang: 'en', renderNav: true }),
});

export type ThemeTokens = z.infer<typeof themeTokensSchema>;
export type SeoMeta = z.infer<typeof seoMetaSchema>;
export type PageEntry = z.infer<typeof pageEntrySchema>;
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
  return [GLOBAL_CSS, ...manifest.pages.flatMap((p) => [pageHtmlPath(p.slug), pageCssPath(p.slug)])];
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
    theme: { colors: {}, fonts: {}, spacing: {}, custom: {} },
    pages: [{ id: 'home', slug: 'home', path: '/', title, seo: { title }, home: true }],
    nav: [],
    settings: { lang: 'en', renderNav: true },
  };
}
