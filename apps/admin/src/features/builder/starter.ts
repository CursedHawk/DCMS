import { BLOCKS_CSS, defaultKit } from '@dcms/gjs-blocks';
import {
  GLOBAL_CSS,
  SITE_JSON,
  emptySiteManifest,
  pageCssPath,
  pageHtmlPath,
  regionHtmlPath,
  renderThemeCss,
  serializeSiteManifest,
  type SiteManifest,
  THEME_CSS,
} from '@dcms/gjs-schema';

// Seeded when an empty StaticPrerender site is first opened in the builder,
// mirroring how the Mode B IDE seeds its React starter: the backend creates an
// empty site and the editor decides what a new one should contain.

/**
 * A new site starts on a real design kit — a full palette, type scale, shadow
 * ramp and set of metrics — rather than on a handful of colours.
 *
 * The difference is what an author sees in the first ten seconds: with a kit,
 * every block they drag already has the site's typography, radius and spacing,
 * and changing the whole look is picking a different kit. Without one, the first
 * job of building a page is writing CSS, which is the thing the builder exists
 * to avoid.
 */
const DEFAULT_THEME: SiteManifest['theme'] = defaultKit().theme;

/**
 * Three bands rather than one, because a page with one section teaches nothing.
 * This is the shape most pages take — a hero, something that explains, a way to
 * act — and its tones alternate, so the first thing an author sees is a page
 * that already reads as designed and can be edited rather than assembled.
 */
const HOME_HTML = `<section class="dcms-hero dcms-section" data-variant="centered" data-align="center">
  <div class="dcms-container">
    <span class="dcms-eyebrow">Your new site</span>
    <h1 class="dcms-hero-title">A headline worth the space</h1>
    <p class="dcms-hero-text">Drag a block from the left panel to start building, pick a different design kit to change the whole look, or switch to the code view and write the markup yourself.</p>
    <div class="dcms-hero-actions">
      <a class="dcms-button" data-size="lg" href="#">Get started</a>
      <a class="dcms-button" data-variant="ghost" data-size="lg" href="#">Learn more</a>
    </div>
  </div>
</section>

<section class="dcms-feature-grid dcms-section" data-variant="cards" data-columns="3" data-tone="alt">
  <div class="dcms-container">
    <header class="dcms-section-head" data-align="center">
      <span class="dcms-eyebrow">What goes here</span>
      <h2>Three things worth saying</h2>
      <p class="dcms-section-lead">Replace this with the reasons someone should care.</p>
    </header>
    <div class="dcms-grid" data-columns="3" data-gap="lg">
      <article class="dcms-feature">
        <h3>The first reason</h3>
        <p>A sentence that makes it concrete rather than impressive.</p>
      </article>
      <article class="dcms-feature">
        <h3>The second</h3>
        <p>A sentence that makes it concrete rather than impressive.</p>
      </article>
      <article class="dcms-feature">
        <h3>And the third</h3>
        <p>A sentence that makes it concrete rather than impressive.</p>
      </article>
    </div>
  </div>
</section>

<section class="dcms-call-to-action dcms-section" data-variant="default">
  <div class="dcms-container">
    <div>
      <h2>Ready when you are</h2>
      <p>One line of reassurance — say what happens after they click.</p>
    </div>
    <div class="dcms-hero-actions">
      <a class="dcms-button" data-size="lg" href="#">Start now</a>
    </div>
  </div>
</section>
`;

/**
 * The site chrome, seeded as shared regions rather than copied into the home
 * page.
 *
 * A navigation pasted into every page is the single most expensive thing a new
 * site can start with: adding one page then means editing all of them. Starting
 * with a header region and a footer region — and a default layout that shows
 * both — means the first page an author adds already has the site's chrome
 * around it, and a menu change is one edit.
 */
const HEADER_HTML = (siteName: string) => `<header class="dcms-navbar" data-layout="between">
  <a class="dcms-logo" href="/">${siteName}</a>
  <input class="dcms-navbar-toggle" id="nav-toggle" type="checkbox" hidden />
  <label class="dcms-navbar-burger" for="nav-toggle" aria-label="Menu"><span></span></label>
  <nav class="dcms-navbar-links" data-dcms-nav="menu">
    <a href="/">Home</a>
  </nav>
</header>
`;

const FOOTER_HTML = (siteName: string) => `<footer class="dcms-footer">
  <div class="dcms-container">
    <nav class="dcms-menu" data-direction="row" data-dcms-nav="menu"><a href="/">Home</a></nav>
    <p class="dcms-footer-note">© ${siteName}</p>
  </div>
</footer>
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
  manifest.regions = [
    { id: 'header', slug: 'header', label: 'Header', placement: 'before' },
    { id: 'footer', slug: 'footer', label: 'Footer', placement: 'after' },
  ];
  manifest.layouts = [
    { id: 'default', label: 'Default', regions: ['header', 'footer'], default: true },
  ];
  // The manifest nav is the older, simpler way to get a menu — a header region
  // renders one already, and two navigations stacked on every page is nobody's
  // intent.
  manifest.settings.renderNav = false;

  return {
    [SITE_JSON]: serializeSiteManifest(manifest),
    [THEME_CSS]: renderThemeCss(manifest.theme),
    [GLOBAL_CSS]: GLOBAL_CSS_CONTENT,
    [pageHtmlPath('home')]: HOME_HTML,
    [pageCssPath('home')]: HOME_CSS_CONTENT,
    [regionHtmlPath('header')]: HEADER_HTML(siteName),
    [regionHtmlPath('footer')]: FOOTER_HTML(siteName),
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
