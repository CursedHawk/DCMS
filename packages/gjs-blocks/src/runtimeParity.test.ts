// @vitest-environment jsdom
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { BIND_TARGETS, TEMPLATE_ATTRS, parseBind } from '@dcms/gjs-schema';
import { describe, expect, it } from 'vitest';
import { BLOCKS_CSS } from './blocks-css';
import { HOME_LABEL_ATTR, NAV_ATTR, normalize } from './nav';
import { PREVIEW_LAYOUTS, previewHtml, type PreviewItem } from './preview';
import { renderTemplate } from './render';
import { navigationSpecs } from './specs';

/**
 * The canvas and the published page must draw the same thing.
 *
 * `preview.ts` runs in the admin bundle; `hydrate.js` ships to every published
 * site and has to stay dependency-free ES5, so the two are deliberately separate
 * implementations of one contract. Nothing enforces that by construction — which
 * means the failure mode is silent: an author lays out a page in the builder,
 * publishes it, and the live site renders something else.
 *
 * This reads the runtime as text and checks the parts of the contract that can
 * be checked without executing it: the layout vocabulary, the prop names, and
 * the class names, which are also what the block stylesheet targets.
 */

const here = dirname(fileURLToPath(import.meta.url));
const HYDRATE = resolve(here, '../../../src/Services/Dcms.SiteBuilder/Runtime/hydrate.js');
const runtime = readFileSync(HYDRATE, 'utf8');
const ASSEMBLER = resolve(here, '../../../src/Services/Dcms.SiteBuilder/StaticSiteAssembler.cs');
const assembler = readFileSync(ASSEMBLER, 'utf8');

/** The `LAYOUTS` array literal declared in hydrate.js. */
function runtimeLayouts(): string[] {
  const match = runtime.match(/var LAYOUTS = \[([^\]]+)\]/);
  expect(match, 'hydrate.js no longer declares a LAYOUTS array').not.toBeNull();
  return [...match![1]!.matchAll(/'([^']+)'/g)].map((m) => m[1]!);
}

describe('hydrate.js and the canvas preview', () => {
  it('offer the same layouts', () => {
    expect(runtimeLayouts()).toEqual(PREVIEW_LAYOUTS);
  });

  it('read the same props', () => {
    // Every prop the preview honours has to reach the published page too, or the
    // setting works in the builder and does nothing once live.
    for (const prop of [
      'heading',
      'subheading',
      'moreLabel',
      'moreHref',
      'layout',
      'arrangement',
      'columns',
      'cardVariant',
      'emptyText',
      'titleField',
      'bodyField',
      'imageField',
      'linkField',
      'metaField',
      'tagsField',
      'excerptLength',
      'linkLabel',
      'itemLink',
    ]) {
      expect(runtime, prop).toContain(`props.${prop}`);
    }
  });

  it('build an item link from the same pattern', () => {
    // A card links to its own page on this site, and that address is built from
    // the item's slug rather than stored in a field — so the pattern has to be
    // expanded identically in both, or a list is clickable in the builder and
    // inert once published.
    const item: PreviewItem = { slug: 'summer-party', data: { title: 'T' } };
    const html = previewHtml([item], { itemLink: '/events/{slug}' });
    expect(html).toContain('href="/events/summer-party"');
    // The shorthand: a pattern with no placeholder is the folder items live in.
    expect(previewHtml([item], { itemLink: '/events/' })).toContain('href="/events/summer-party"');
    // An explicit "none" still wins, or there would be no way to say "not a link".
    expect(previewHtml([item], { itemLink: '/events/', linkField: '-' })).not.toContain('href=');

    for (const marker of ['expandLink', 'linkFor']) {
      expect(runtime, `hydrate.js is missing ${marker}`).toContain(`function ${marker}`);
    }
  });

  it('agree on the third field-mapping state', () => {
    expect(runtime).toContain("var FIELD_NONE = '-'");
  });

  it('both look tenant custom fields up by path', () => {
    // Custom fields are nested under one key, so a flat lookup finds only the
    // fields every tenant shares — the reason field mapping looked broken on any
    // site that had configured its own.
    expect(runtime).toContain('function readField');
    expect(runtime).toContain("path.indexOf('.')");
  });

  it('emit the same class names', () => {
    // The preview's output is the reference: run every layout, collect the
    // classes, and require the runtime's source to mention each one.
    const item: PreviewItem = {
      data: {
        title: 'A title',
        excerpt: 'Some text',
        coverImage: 'a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d',
        source: '/a.mp3',
        linkUrl: '/x',
        publishedAt: '2025-01-01',
        tags: ['one'],
      },
    };
    const emitted = new Set<string>();
    for (const layout of PREVIEW_LAYOUTS) {
      const html = previewHtml([item, item], {
        layout,
        heading: 'H',
        subheading: 'S',
        moreLabel: 'All',
        moreHref: '/all',
        columns: '3',
        arrangement: 'masonry',
      });
      for (const match of html.matchAll(/class="([^"]+)"/g)) {
        for (const cls of match[1]!.split(/\s+/)) emitted.add(cls);
      }
    }

    expect(emitted.size).toBeGreaterThan(15);
    for (const cls of emitted) {
      expect(runtime, `hydrate.js never emits .${cls}`).toContain(cls);
    }
  });

  it('style every class the preview emits from the site stylesheet', () => {
    // If the block stylesheet does not target a class, plugin content falls back
    // to hydrate.js's zero-specificity sheet forever and never follows the kit.
    const html = previewHtml([{ data: { title: 'T', excerpt: 'E', coverImage: '/i.png', tags: ['a'] } }], {
      heading: 'H',
      moreLabel: 'All',
      moreHref: '/all',
    });
    for (const match of html.matchAll(/class="([^"]+)"/g)) {
      for (const cls of match[1]!.split(/\s+/)) {
        expect(BLOCKS_CSS, `blocks-css does not style .${cls}`).toContain(`.${cls}`);
      }
    }
  });

  it('ships a fallback sheet that cannot beat the site’s own CSS', () => {
    const sheet = runtime.slice(
      runtime.indexOf('function injectStylesOnce'),
      runtime.indexOf('function linked'),
    );
    // Every selector at zero specificity, so load order stops mattering.
    const selectors = [...sheet.matchAll(/'(@media[^']*\{)?(:?[^'{]*)\{/g)]
      .map((m) => m[2]!.trim())
      .filter((s) => s.length > 0 && !s.startsWith('@'));
    expect(selectors.length).toBeGreaterThan(20);
    for (const selector of selectors) {
      expect(selector, `${selector} is not wrapped in :where()`).toMatch(/^:where\(/);
    }
    // And it must not reach for variables the theme does not emit — the bug this
    // check exists for: every plugin block ignoring the site theme because the
    // fallback asked for `--color-border` while the theme emits `--dcms-color-border`.
    const vars = [...sheet.matchAll(/var\((--[a-z0-9-]+)/g)].map((m) => m[1]!);
    expect(vars.length).toBeGreaterThan(5);
    for (const name of vars) {
      expect(name, `${name} is not a theme token`).toMatch(/^--dcms-/);
    }
  });
});

/**
 * The tenant component contract.
 *
 * A component the author built themselves is laid out in the builder and drawn
 * again by the runtime, from the same template. The two renderers are separate
 * implementations, so every attribute and every bind target has to exist in both
 * — an attribute the builder writes and the runtime ignores is a component that
 * works in the canvas and renders blank once published.
 */
describe('the tenant component renderer', () => {
  it('knows every template attribute the schema defines', () => {
    for (const attr of TEMPLATE_ATTRS) {
      expect(runtime, `hydrate.js does not read ${attr}`).toContain(`'${attr}'`);
    }
  });

  it('knows every bind target the schema defines', () => {
    const declared = runtime.match(/var BIND_TARGETS = \[([^\]]+)\]/);
    expect(declared, 'hydrate.js no longer declares BIND_TARGETS').not.toBeNull();
    const runtimeTargets = [...declared![1]!.matchAll(/'([^']+)'/g)].map((m) => m[1]!);
    expect([...runtimeTargets].sort()).toEqual([...BIND_TARGETS].sort());
  });

  it('parses a bind the same way the schema does', () => {
    // Longest-first matching, or `style:background-image` reads as `style`.
    expect(parseBind('text:title')).toEqual({ target: 'text', source: 'title' });
    expect(parseBind('style:background-image:cover')).toEqual({
      target: 'style:background-image',
      source: 'cover',
    });
    expect(parseBind('title')).toEqual({ target: 'text', source: 'title' });
    const targets = runtime.match(/var BIND_TARGETS = \[([^\]]+)\]/)![1]!;
    expect(targets.indexOf("'style:background-image'")).toBeLessThan(targets.indexOf("'text'"));
  });

  it('renders a template the runtime could render the same way', () => {
    // The canvas's output is the reference: whatever it produces, the runtime
    // has to have the code path that produces it.
    const html = renderTemplate(
      document,
      '<div><h2 data-dcms-bind="text:@heading"></h2>' +
        '<article data-dcms-repeat><img data-dcms-bind="src:cover" data-dcms-if="cover" />' +
        '<h3 data-dcms-bind="text:title"></h3></article>' +
        '<p data-dcms-empty>None</p></div>',
      [{ data: { title: 'A', cover: '/a.png' } }],
      { heading: 'Latest' },
    );
    expect(html).toContain('Latest');
    expect(html).not.toContain('None');
    expect(html).not.toContain('data-dcms-');
    for (const marker of ['renderTemplate', 'applyBindings', 'stripTemplateAttrs', 'definitionFor']) {
      expect(runtime, `hydrate.js is missing ${marker}`).toContain(`function ${marker}`);
    }
  });

  it('expands nested repeats against the item that holds them', () => {
    // One list inside another — an event's crew under each event — is the shape
    // a tenant component reaches for as soon as its content is not flat. Only
    // the first repeat in the whole template used to be expanded, so the inner
    // one published as a single unbound row.
    const html = renderTemplate(
      document,
      '<div><article data-dcms-repeat><h3 data-dcms-bind="text:title"></h3>' +
        '<ul><li data-dcms-repeat="crew" data-dcms-bind="text:name"></li></ul></article></div>',
      [
        { data: { title: 'Opening', crew: [{ name: 'Ada' }, { name: 'Bo' }] } },
        { data: { title: 'Closing', crew: [{ name: 'Cy' }] } },
      ],
    );
    expect(html).toContain('Opening');
    expect(html).toContain('Closing');
    // Each event's own crew, not the first event's under both.
    expect(html.indexOf('Ada')).toBeLessThan(html.indexOf('Closing'));
    expect(html.indexOf('Cy')).toBeGreaterThan(html.indexOf('Closing'));
    expect(html).not.toContain('data-dcms-');

    for (const marker of ['expandRepeats', 'outermostRepeats', 'asItems', 'resolveMeta']) {
      expect(runtime, `hydrate.js is missing ${marker}`).toContain(`function ${marker}`);
    }
  });

  it('resolves the same positional sources in both', () => {
    const html = renderTemplate(
      document,
      '<div><span data-dcms-repeat data-dcms-bind="class:#parity">' +
        '<b data-dcms-bind="text:#number"></b></span></div>',
      [{ data: {} }, { data: {} }, { data: {} }],
    );
    expect(html).toContain('class="even"');
    expect(html).toContain('class="odd"');
    expect(html).toContain('<b>1</b>');
    expect(html).toContain('<b>3</b>');
    for (const key of ['index', 'number', 'count', 'first', 'last', 'even', 'odd', 'parity']) {
      expect(runtime, `hydrate.js does not know #${key}`).toContain(`'${key}'`);
    }
  });

  it('reads its component registry from the page the assembler writes it to', () => {
    expect(runtime).toContain("getElementById('dcms-components')");
    expect(assembler, 'the assembler never emits the registry').toContain('dcms-components');
  });
});

/**
 * Shared regions are the second place the two renderers have to agree: the
 * builder paints them around the canvas, the assembler writes them into the
 * document, and the navigation inside them is resolved per page at run time. A
 * breadcrumb bar that works in the builder and shows the home page's trail on
 * every published page is exactly the kind of drift this catches.
 */
describe('page-aware navigation', () => {
  it('is driven by the same attribute in both renderers', () => {
    expect(runtime).toContain("var NAV_ATTR = 'data-dcms-nav'");
    expect(NAV_ATTR).toBe('data-dcms-nav');
    expect(runtime).toContain("var HOME_LABEL_ATTR = 'data-home-label'");
    expect(HOME_LABEL_ATTR).toBe('data-home-label');
  });

  it('handles both kinds the canvas handles', () => {
    for (const kind of ['breadcrumbs', 'menu']) {
      expect(runtime, `hydrate.js ignores data-dcms-nav="${kind}"`).toContain(`'${kind}'`);
    }
    for (const marker of ['breadcrumbTrail', 'fillBreadcrumbs', 'markCurrent', 'applyNav']) {
      expect(runtime, `hydrate.js is missing ${marker}`).toContain(`function ${marker}`);
    }
  });

  it('reads its route table from the page the assembler writes it to', () => {
    expect(runtime).toContain("getElementById('dcms-routes')");
    expect(assembler, 'the assembler never emits the route table').toContain('dcms-routes');
  });

  it('normalizes a path the same way in both', () => {
    // The runtime strips the same three things; if it stops, a link to
    // `/blog/` no longer marks `/blog` as the current page.
    expect(normalize('/blog/')).toBe('/blog');
    expect(runtime).toContain("replace(/index.html$/, '')");
    expect(runtime).toContain("function normalizePath");
  });

  it('is emitted by every builder snippet that needs it', () => {
    const breadcrumbs = navigationSpecs.find((s) => s.type === 'Breadcrumbs')!;
    expect(breadcrumbs.snippet).toContain('data-dcms-nav="breadcrumbs"');
    const menu = navigationSpecs.find((s) => s.type === 'Menu')!;
    expect(menu.snippet).toContain('data-dcms-nav="menu"');
  });
});

/** Regions are assembled into the document, not published as files. */
describe('shared regions', () => {
  it('are consumed by the assembler rather than served', () => {
    expect(assembler).toContain('RegionsPrefix');
    expect(assembler).toContain('SourceOnlyPrefixes');
  });

  it('are wrapped in the class the builder styles them by', () => {
    expect(assembler).toContain('dcms-region');
  });
});
