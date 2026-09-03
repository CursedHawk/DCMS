// Source trees for the two git-backed site render modes.
//
// Mode C (StaticFiles) needs none of this: its "source" is a zip of already-built files,
// and `assets.mjs` makes one. Modes A and B are authored, committed to Forgejo and built,
// so a fixture for them has to be a project a build can actually succeed on.
//
// Mode A is generated here rather than imported from the admin SPA's `starterFiles()`
// (apps/admin/src/features/builder/starter.ts). That function is the canonical starter and
// this is deliberately not it, for two reasons: it lives in a TypeScript app that would
// have to be built before the harness could run, and a one-page starter is the wrong
// fixture anyway — the thing being measured is the assembler's cost per page, which a
// single page cannot show. What is shared with it is the shape: a v2 manifest, a header
// and footer region, one default layout, theme + global CSS, and `pages/<slug>.html`.
//
// Mode B is NOT generated here at all. `GET /api/admin/sites/{id}/starter-files?flavor=starter`
// returns the real scaffold the IDE seeds a new React app with — package.json, lockfile,
// vite config, a typed API client generated from that tenant's own OpenAPI — and building
// something else would measure a project no tenant has.

/** Mirrors StaticSiteAssembler's constants (src/Services/Dcms.SiteBuilder/StaticSiteAssembler.cs). */
const SITE_JSON = 'site.json';
const THEME_CSS = 'styles/theme.css';
const GLOBAL_CSS = 'styles/global.css';
const pageHtmlPath = (slug) => `pages/${slug}.html`;
const pageCssPath = (slug) => `styles/pages/${slug}.css`;
const regionHtmlPath = (slug) => `regions/${slug}.html`;

/** Version 2 is the file-map format. A v1 manifest fails the build on purpose. */
const MANIFEST_VERSION = 2;

const THEME = {
  colors: {
    surface: '#ffffff',
    'surface-alt': '#f4f6f8',
    ink: '#16202a',
    'ink-muted': '#5a6b7b',
    accent: '#2f6f5f',
    'accent-ink': '#ffffff',
    line: '#dfe5ea',
  },
  fonts: {
    body: "'Inter', system-ui, sans-serif",
    heading: "'Inter', system-ui, sans-serif",
  },
  spacing: { section: '4rem', gutter: '1.5rem' },
  radius: '10px',
  custom: {},
};

/**
 * One page's markup. Sized to be representative rather than minimal: the assembler wraps
 * each page in a document shell, and a page of one paragraph would make the shell look
 * like the whole cost.
 */
function pageHtml(index, title) {
  const cards = Array.from({ length: 6 }, (_, i) => `
      <article class="card">
        <h3>Point ${i + 1}</h3>
        <p>Fixture copy for load measurement. Page ${index}, card ${i + 1}. Long enough that
        the assembled document is a realistic size rather than a stub.</p>
      </article>`).join('');

  return `<section class="hero">
  <div class="container">
    <span class="eyebrow">Section ${index}</span>
    <h1>${title}</h1>
    <p class="lead">A fixture page published by the DCMS load harness. Everything on it is
    static markup, which is what Mode A ships: the builder writes HTML and the site-builder
    wraps it in the document shell.</p>
    <a class="button" href="/">Back to the start</a>
  </div>
</section>

<section class="grid-section">
  <div class="container">
    <h2>What this page is for</h2>
    <div class="grid">${cards}
    </div>
  </div>
</section>
`;
}

const HEADER_HTML = (siteName, pages) => `<header class="site-header">
  <div class="container">
    <a class="brand" href="/">${siteName}</a>
    <nav class="site-nav">
${pages.map((p) => `      <a href="${p.path}">${p.title}</a>`).join('\n')}
    </nav>
  </div>
</header>
`;

const FOOTER_HTML = (siteName) => `<footer class="site-footer">
  <div class="container">
    <p>${siteName} — a load-test fixture. Nothing here is a real site.</p>
  </div>
</footer>
`;

const GLOBAL_CSS_CONTENT = `*, *::before, *::after { box-sizing: border-box; }
body { margin: 0; font-family: var(--font-body); color: var(--color-ink); background: var(--color-surface); }
.container { max-width: 68rem; margin: 0 auto; padding: 0 var(--space-gutter); }
.site-header { border-bottom: 1px solid var(--color-line); }
.site-header .container { display: flex; gap: 1.5rem; align-items: center; padding-block: 1rem; }
.brand { font-weight: 600; text-decoration: none; color: inherit; }
.site-nav { display: flex; gap: 1rem; flex-wrap: wrap; }
.site-nav a { color: var(--color-ink-muted); text-decoration: none; }
.hero { padding-block: var(--space-section); background: var(--color-surface-alt); }
.hero h1 { font-family: var(--font-heading); font-size: clamp(2rem, 5vw, 3rem); margin: 0.5rem 0; }
.eyebrow { text-transform: uppercase; letter-spacing: 0.08em; font-size: 0.75rem; color: var(--color-ink-muted); }
.lead { color: var(--color-ink-muted); max-width: 40rem; }
.button { display: inline-block; padding: 0.7rem 1.2rem; border-radius: var(--radius); background: var(--color-accent); color: var(--color-accent-ink); text-decoration: none; }
.grid-section { padding-block: var(--space-section); }
.grid { display: grid; gap: 1.5rem; grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr)); }
.card { border: 1px solid var(--color-line); border-radius: var(--radius); padding: 1.25rem; }
.card h3 { margin-top: 0; }
.site-footer { border-top: 1px solid var(--color-line); padding-block: 2rem; color: var(--color-ink-muted); }
`;

/** Theme tokens as custom properties, the same shape renderThemeCss emits. */
function renderThemeCss(theme) {
  const lines = [':root {'];
  for (const [k, v] of Object.entries(theme.colors)) lines.push(`  --color-${k}: ${v};`);
  for (const [k, v] of Object.entries(theme.fonts)) lines.push(`  --font-${k}: ${v};`);
  for (const [k, v] of Object.entries(theme.spacing)) lines.push(`  --space-${k}: ${v};`);
  if (theme.radius) lines.push(`  --radius: ${theme.radius};`);
  for (const [k, v] of Object.entries(theme.custom ?? {})) lines.push(`  ${k}: ${v};`);
  lines.push('}');
  return lines.join('\n') + '\n';
}

/**
 * The file map for a Mode A (StaticPrerender) fixture site.
 *
 * `pageCount` is the knob that matters. A Mode A build is pure string assembly in C# —
 * no Node, no sandbox — so its cost is very nearly linear in pages, and the question the
 * sitebuild scenario asks of Mode A is where that line sits relative to a Mode C extract
 * of the same size.
 */
export function modeASource(siteName, pageCount = 12) {
  const pages = [{ id: 'home', slug: 'home', path: '/', title: siteName, seo: { title: siteName }, home: true }];
  for (let i = 2; i <= pageCount; i++) {
    const slug = `page-${String(i).padStart(2, '0')}`;
    const title = `Section ${i}`;
    pages.push({ id: slug, slug, path: `/${slug}`, title, seo: { title, description: `Fixture page ${i}.` } });
  }

  const manifest = {
    version: MANIFEST_VERSION,
    theme: THEME,
    pages,
    // The header region renders the menu, so the manifest nav would be a second one.
    nav: [],
    regions: [
      { id: 'header', slug: 'header', label: 'Header', placement: 'before' },
      { id: 'footer', slug: 'footer', label: 'Footer', placement: 'after' },
    ],
    layouts: [{ id: 'default', label: 'Default', regions: ['header', 'footer'], default: true }],
    settings: {
      lang: 'en',
      renderNav: false,
      // No banner: the fixture sites record nothing, and a consent dialog on every page
      // would be markup the measurement has to carry for no reason.
      cookieConsent: { mode: 'off' },
    },
  };

  const files = {
    [SITE_JSON]: JSON.stringify(manifest, null, 2),
    [THEME_CSS]: renderThemeCss(THEME),
    [GLOBAL_CSS]: GLOBAL_CSS_CONTENT,
    [regionHtmlPath('header')]: HEADER_HTML(siteName, pages.slice(0, 6)),
    [regionHtmlPath('footer')]: FOOTER_HTML(siteName),
  };
  for (const page of pages) {
    files[pageHtmlPath(page.slug)] = pageHtml(pages.indexOf(page) + 1, page.title);
    files[pageCssPath(page.slug)] = `/* Styles specific to ${page.slug}. */\n`;
  }
  return { files, pageCount: pages.length };
}
