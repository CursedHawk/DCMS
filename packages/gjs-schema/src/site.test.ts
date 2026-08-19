import { describe, expect, it } from 'vitest';
import { z } from 'zod';
import { siteManifestSchema } from './site';
import {
  emptySiteManifest,
  emptyTheme,
  expectedFiles,
  homePage,
  makePageEntry,
  parseSiteManifest,
  safeParseSiteManifest,
  serializeSiteManifest,
} from './site';

describe('siteManifestSchema', () => {
  it('round-trips an empty manifest through serialize/parse', () => {
    const manifest = emptySiteManifest();
    expect(parseSiteManifest(JSON.parse(serializeSiteManifest(manifest)))).toEqual(manifest);
  });

  it('applies defaults for omitted optional sections', () => {
    const parsed = parseSiteManifest({
      version: 2,
      pages: [{ id: 'p1', slug: 'home', path: '/', title: 'Home', seo: { title: 'Home' } }],
    });
    expect(parsed.nav).toEqual([]);
    expect(parsed.settings).toEqual({ lang: 'en', renderNav: true });
    expect(parsed.theme).toEqual(emptyTheme());
  });

  it('rejects a version 1 (component tree) definition rather than loading it empty', () => {
    const legacy = { version: 1, theme: { colors: {}, fonts: {} }, pages: [], nav: [] };
    expect(safeParseSiteManifest(legacy).success).toBe(false);
  });

  it('rejects a page path that is not rooted', () => {
    const result = safeParseSiteManifest({
      version: 2,
      pages: [{ id: 'p1', slug: 'home', path: 'home', title: 'Home', seo: { title: 'Home' } }],
    });
    expect(result.success).toBe(false);
  });

  it('rejects a slug that would not be a safe file name', () => {
    const result = safeParseSiteManifest({
      version: 2,
      pages: [{ id: 'p1', slug: '../etc', path: '/', title: 'Home', seo: { title: 'Home' } }],
    });
    expect(result.success).toBe(false);
  });

  it('requires at least one page', () => {
    expect(safeParseSiteManifest({ version: 2, pages: [] }).success).toBe(false);
  });

  it('accepts nested navigation', () => {
    const parsed = parseSiteManifest({
      version: 2,
      pages: [{ id: 'p1', slug: 'home', path: '/', title: 'Home', seo: { title: 'Home' } }],
      nav: [{ label: 'About', path: '/about', children: [{ label: 'Team', path: '/about/team' }] }],
    });
    expect(parsed.nav[0].children?.[0].label).toBe('Team');
  });
});

describe('JSON Schema export', () => {
  // The code view validates site.json inside Monaco's json worker using a JSON
  // Schema derived from this very schema, so the editor cannot accept a manifest
  // the builder would then reject. If the conversion breaks (recursive nav is
  // the risky part), that guarantee silently disappears.
  it('converts to a draft-7 schema, including the recursive nav', () => {
    const schema = z.toJSONSchema(siteManifestSchema, { io: 'input', target: 'draft-7' }) as {
      type?: string;
      properties?: Record<string, unknown>;
    };

    expect(schema.type).toBe('object');
    expect(Object.keys(schema.properties ?? {})).toEqual(
      expect.arrayContaining(['version', 'theme', 'pages', 'nav', 'settings']),
    );
    // The recursive nav has to survive as a reference rather than blowing the stack.
    expect(JSON.stringify(schema)).toContain('nav');
  });
});

describe('homePage', () => {
  it('prefers the explicitly flagged home page', () => {
    const manifest = parseSiteManifest({
      version: 2,
      pages: [
        { id: 'a', slug: 'about', path: '/about', title: 'About', seo: { title: 'About' } },
        { id: 'h', slug: 'start', path: '/start', title: 'Start', seo: { title: 'Start' }, home: true },
      ],
    });
    expect(homePage(manifest).id).toBe('h');
  });

  it('falls back to the page at / then to the first page', () => {
    const rooted = parseSiteManifest({
      version: 2,
      pages: [
        { id: 'a', slug: 'about', path: '/about', title: 'About', seo: { title: 'About' } },
        { id: 'r', slug: 'root', path: '/', title: 'Root', seo: { title: 'Root' } },
      ],
    });
    expect(homePage(rooted).id).toBe('r');

    const neither = parseSiteManifest({
      version: 2,
      pages: [{ id: 'a', slug: 'about', path: '/about', title: 'About', seo: { title: 'About' } }],
    });
    expect(homePage(neither).id).toBe('a');
  });
});

describe('makePageEntry', () => {
  it('derives a unique slug from the route path', () => {
    const manifest = emptySiteManifest();
    const entry = makePageEntry(manifest, '/', 'Home again', 'p2');
    expect(entry.slug).toBe('home-2');
    expect(entry.seo.title).toBe('Home again');
  });
});

describe('expectedFiles', () => {
  it('lists global css plus each page html and css', () => {
    expect(expectedFiles(emptySiteManifest())).toEqual([
      'styles/global.css',
      'pages/home.html',
      'styles/pages/home.css',
    ]);
  });
});
