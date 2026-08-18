import { BLOCKS_CSS } from '@dcms/gjs-blocks';
import {
  GLOBAL_CSS,
  SITE_JSON,
  emptySiteManifest,
  pageCssPath,
  pageHtmlPath,
  renderThemeCss,
  serializeSiteManifest,
  THEME_CSS,
  type SiteManifest,
} from '@dcms/gjs-schema';

// Seeded when an empty StaticPrerender site is first opened in the builder,
// mirroring how the Mode B IDE seeds its React starter: the backend creates an
// empty site and the editor decides what a new one should contain.
//
// The starter is deliberately small — one real section rather than a template —
// so the first thing an author does is add their own blocks, not delete ours.

const DEFAULT_THEME: SiteManifest['theme'] = {
  colors: {
    brand: '#2563eb',
    'brand-contrast': '#ffffff',
    text: '#0f172a',
    muted: '#64748b',
    surface: '#ffffff',
    'surface-alt': '#f8fafc',
    border: '#e2e8f0',
  },
  fonts: {
    body: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
    heading: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
  },
  spacing: { sm: '0.5rem', md: '1rem', lg: '2rem', xl: '4rem' },
  radius: '0.5rem',
  custom: {},
};

const HOME_HTML = `<section class="dcms-section dcms-hero">
  <div class="dcms-container">
    <h1 class="dcms-hero-title">Your new site</h1>
    <p class="dcms-hero-text">Drag a block from the left panel to start building, or switch to the code view to write the markup yourself.</p>
    <a class="dcms-button" href="#">Get started</a>
  </div>
</section>
`;

/**
 * Base rules every site starts with: the block library's own defaults, which
 * style every block in the palette against the theme tokens. They are copied
 * into the site's own stylesheet rather than linked from the package, so the
 * author can edit any of it and a published page needs no build step.
 */
const GLOBAL_CSS_CONTENT = BLOCKS_CSS;

/**
 * Page-scoped rules. Empty to start: the block defaults already style the home
 * page, and seeding overrides here would teach the wrong habit — page CSS is for
 * what is genuinely specific to one page.
 */
const HOME_CSS_CONTENT = `/* Styles that apply only to this page. */
`;

/** The file map a brand-new Mode A site is seeded with. */
export function starterFiles(siteName = 'Home'): Record<string, string> {
  const manifest = emptySiteManifest(siteName);
  manifest.theme = DEFAULT_THEME;
  manifest.nav = [{ label: siteName, path: '/' }];

  return {
    [SITE_JSON]: serializeSiteManifest(manifest),
    [THEME_CSS]: renderThemeCss(manifest.theme),
    [GLOBAL_CSS]: GLOBAL_CSS_CONTENT,
    [pageHtmlPath('home')]: HOME_HTML,
    [pageCssPath('home')]: HOME_CSS_CONTENT,
  };
}

/**
 * True when the loaded file map is not a Mode A project yet — a brand-new site,
 * or one whose repo holds only the auto-created README. Either way the builder
 * seeds the starter rather than opening an empty canvas.
 */
export function needsStarter(files: Record<string, string>): boolean {
  return files[SITE_JSON] === undefined;
}
