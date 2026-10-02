import { describe, expect, it } from 'vitest';
import { DESIGN_KITS, matchKit } from '@dcms/gjs-blocks';
import { safeParseSiteManifest, serializeSiteManifest } from '@dcms/gjs-schema';
import { useVfs } from '../../site-source/vfs';
import { STATIC_TOOLS, checkStaticSite, formatStaticReport } from './staticTools';

/**
 * The Mode A gate.
 *
 * <p>These are the cases that make the difference between a generated site that opens and one
 * that drops the builder to an error banner — the ones an agent writing four files in a row
 * actually produces. Each is asserted on the <b>errors/warnings split</b>, not just on "said
 * something", because the split is what decides whether the runtime spends a repair turn.</p>
 */

const page = (title: string, slug: string, path: string) => ({
  id: slug,
  slug,
  path,
  title,
  seo: { title },
});

function manifest(pages: ReturnType<typeof page>[], nav: { label: string; path: string }[] = []) {
  return JSON.stringify({ version: 2, pages, nav });
}

const HOME = page('Home', 'home', '/');

/** The smallest file map that passes. */
function goodSite(): Record<string, string> {
  return {
    'site.json': manifest([HOME]),
    'pages/home.html': '<section class="dcms-hero"><h1>Hello</h1></section>',
    'styles/global.css': ':root { }',
  };
}

describe('checkStaticSite', () => {
  it('passes a consistent site', () => {
    const report = checkStaticSite(goodSite());
    expect(report).toEqual({ errors: [], warnings: [] });
    expect(formatStaticReport(report)).toContain('consistent');
  });

  it('fails a page listed in the manifest with no file', () => {
    const files = goodSite();
    files['site.json'] = manifest([HOME, page('About', 'about', '/about')]);
    expect(checkStaticSite(files).errors).toEqual([
      '“About” is listed in site.json but pages/about.html does not exist.',
    ]);
  });

  it('fails a page file with no manifest entry — it would never ship', () => {
    const files = goodSite();
    files['pages/about.html'] = '<section>About</section>';
    expect(checkStaticSite(files).errors[0]).toContain('pages/about.html has no entry');
  });

  it('fails an empty page file rather than publishing a blank page', () => {
    const files = goodSite();
    files['pages/home.html'] = '   \n';
    expect(checkStaticSite(files).errors[0]).toContain('would publish as a blank page');
  });

  it('fails a site with no home page', () => {
    const files = {
      'site.json': manifest([page('About', 'about', '/about')]),
      'pages/about.html': '<section>About</section>',
      'styles/global.css': '',
    };
    expect(checkStaticSite(files).errors).toContain(
      'No page is published at “/”. The site has no home page.',
    );
  });

  it('reports a manifest that does not parse, and nothing else', () => {
    const files = goodSite();
    files['site.json'] = '{ "version": 2, ';
    const report = checkStaticSite(files);
    expect(report.errors).toHaveLength(1);
    expect(report.errors[0]).toContain('not valid JSON');
  });

  it('reports a manifest that parses but is not a site, and nothing else', () => {
    const files = goodSite();
    // No `pages`, so every consistency check below it would fire on an absence.
    files['site.json'] = JSON.stringify({ version: 2 });
    const report = checkStaticSite(files);
    expect(report.errors.every((e) => e.startsWith('site.json —'))).toBe(true);
  });

  it('fails a page file carrying a document wrapper or a script', () => {
    const files = goodSite();
    files['pages/home.html'] = '<body><h1>Hi</h1><script>alert(1)</script></body>';
    const errors = checkStaticSite(files).errors;
    expect(errors.some((e) => e.includes('body fragment'))).toBe(true);
    expect(errors.some((e) => e.includes('<script>'))).toBe(true);
  });

  it('warns — but does not fail — on inline styles and images with no alt', () => {
    const files = goodSite();
    files['pages/home.html'] = '<section style="color:red"><img src="/a.png"></section>';
    const report = checkStaticSite(files);
    // Both are real problems and neither is worth burning a repair turn on.
    expect(report.errors).toEqual([]);
    expect(report.warnings.some((w) => w.includes('style=""'))).toBe(true);
    expect(report.warnings.some((w) => w.includes('no alt'))).toBe(true);
  });

  it('accepts an img that does carry alt', () => {
    const files = goodSite();
    files['pages/home.html'] = '<img src="/a.png" alt="A thing">';
    expect(checkStaticSite(files).warnings).toEqual([]);
  });

  it('warns about nav pointing nowhere, and accepts a detail route match', () => {
    const files = {
      'site.json': manifest(
        [HOME, page('Event', 'events-slug', '/events/:slug')],
        [
          { label: 'Home', path: '/' },
          { label: 'An event', path: '/events/spring' },
          { label: 'Shop', path: '/shop' },
        ],
      ),
      'pages/home.html': '<h1>Hi</h1>',
      'pages/events-slug.html': '<article></article>',
      'styles/global.css': '',
    };
    const report = checkStaticSite(files);
    expect(report.errors).toEqual([]);
    // `/events/spring` is served by `/events/:slug`; `/shop` is served by nothing.
    expect(report.warnings).toEqual(['site.json.nav links to “/shop”, which no page serves.']);
  });

  it('reports a missing site.json on its own', () => {
    expect(checkStaticSite({ 'pages/home.html': '<h1>Hi</h1>' }).errors).toEqual([
      'site.json is missing. Without it there is no site.',
    ]);
  });
});

/**
 * `apply_design_kit`.
 *
 * <p>Asserted because it is the one Mode A tool that writes without being asked to write a
 * file: it reads `site.json`, swaps the theme and patches it back. The failure worth defending
 * against is the quiet one — a manifest it cannot read, where doing nothing and saying nothing
 * would leave the model believing the site had been restyled.</p>
 */
describe('apply_design_kit', () => {
  const tool = STATIC_TOOLS.find((t) => t.name === 'apply_design_kit')!;

  /** Only `tx.patch` is reached, so only `tx.patch` is stubbed. */
  function fakeContext() {
    const patches: { path: string; newText: string }[] = [];
    const tx = {
      patch: (path: string, args: { newText: string }) => {
        patches.push({ path, newText: args.newText });
        return { content: `Patched ${path}`, paths: [path] };
      },
    };
    return { ctx: { tx } as never, patches };
  }

  function seed(siteJson: string) {
    useVfs.setState({ files: { 'site.json': siteJson } });
  }

  it('rewrites site.json.theme with the kit and leaves everything else alone', async () => {
    const manifest = {
      version: 2,
      pages: [{ id: 'home', slug: 'home', path: '/', title: 'Home', seo: { title: 'Home' } }],
      nav: [{ label: 'Home', path: '/' }],
    };
    seed(JSON.stringify(manifest));
    const { ctx, patches } = fakeContext();

    const out = await tool.run({ kit: DESIGN_KITS[0].id }, ctx);

    expect(out.isError).toBeFalsy();
    expect(patches).toHaveLength(1);
    expect(patches[0].path).toBe('site.json');
    const written = JSON.parse(patches[0].newText) as typeof manifest & { theme: unknown };
    expect(written.pages).toEqual(manifest.pages);
    expect(written.nav).toEqual(manifest.nav);
    expect(matchKit(written.theme as never)?.id).toBe(DESIGN_KITS[0].id);
  });

  it('refuses rather than writing when site.json cannot be read as a manifest', async () => {
    seed('{ "version": 2, ');
    const { ctx, patches } = fakeContext();

    const out = await tool.run({ kit: DESIGN_KITS[0].id }, ctx);

    expect(out.isError).toBe(true);
    expect(patches).toEqual([]);
  });

  it('says so instead of writing when the kit is already applied', async () => {
    const kit = DESIGN_KITS[0];
    const base = {
      version: 2,
      pages: [{ id: 'home', slug: 'home', path: '/', title: 'Home', seo: { title: 'Home' } }],
    };
    seed(serializeSiteManifest(safeParseSiteManifest({ ...base, theme: kit.theme }).data!));
    const { ctx, patches } = fakeContext();

    const out = await tool.run({ kit: kit.id }, ctx);

    expect(out.isError).toBeFalsy();
    expect(out.content).toContain('already on');
    expect(patches).toEqual([]);
  });
});
