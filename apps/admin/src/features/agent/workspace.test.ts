import { describe, expect, it } from 'vitest';
import { cachedHash, hashContent } from './hash';
import { buildIndex } from './projectIndex';
import {
  createWorkspace,
  DEFAULT_MAX_HITS,
  formatHits,
  formatTree,
  MAX_WHOLE_FILE_LINES,
  type ReadError,
} from './workspace';

const META = { siteId: 's1', branch: 'main', revision: 7 };

const lines = (n: number, text = 'x') =>
  Array.from({ length: n }, (_, i) => `${text}${i + 1}`).join('\n');

const ws = (files: Record<string, string>) => createWorkspace(files, META);

describe('hash', () => {
  it('is stable and distinguishes content', () => {
    expect(hashContent('hello')).toBe(hashContent('hello'));
    expect(hashContent('hello')).not.toBe(hashContent('hellp'));
  });

  it('memoises without changing the answer', () => {
    expect(cachedHash('abc')).toBe(hashContent('abc'));
    expect(cachedHash('abc')).toBe(hashContent('abc'));
  });

  it('distinguishes the empty string from a missing file', () => {
    // A file that exists and is empty must still get a hash; null is reserved for "not there".
    expect(ws({ 'a.ts': '' }).hashOf('a.ts')).toEqual(expect.any(String));
    expect(ws({ 'a.ts': '' }).hashOf('b.ts')).toBeNull();
  });
});

describe('tree', () => {
  const files = {
    'src/App.tsx': 'a',
    'src/components/Hero.tsx': 'b',
    'src/components/Nav.tsx': 'c',
    'package.json': '{}',
    'index.html': '<html>',
  };

  it('summarises directories instead of listing every path', () => {
    const entries = ws(files).tree();
    expect(entries).toEqual([
      { name: 'src', kind: 'dir', files: 3 },
      { name: 'index.html', kind: 'file', size: 6 },
      { name: 'package.json', kind: 'file', size: 2 },
    ]);
  });

  it('descends into a prefix', () => {
    expect(ws(files).tree('src')).toEqual([
      { name: 'components', kind: 'dir', files: 2 },
      { name: 'App.tsx', kind: 'file', size: 1 },
    ]);
  });

  it('tolerates a trailing slash on the prefix', () => {
    expect(ws(files).tree('src/')).toEqual(ws(files).tree('src'));
  });

  it('formats compactly', () => {
    expect(formatTree(ws(files).tree())).toBe('src/ (3 files)\nindex.html\npackage.json');
    expect(formatTree([], 'nope')).toBe('(nothing under nope)');
    expect(formatTree([])).toBe('(empty site)');
  });
});

describe('read', () => {
  it('returns a whole small file with its line count', () => {
    const r = ws({ 'a.ts': 'one\ntwo' }).read('a.ts');
    expect(r).toMatchObject({ path: 'a.ts', content: 'one\ntwo', totalLines: 2 });
    expect(r).not.toHaveProperty('range');
  });

  it('reports a missing file as an error rather than empty content', () => {
    expect((ws({}).read('nope.ts') as ReadError).error).toContain('not found');
  });

  it('refuses binary files', () => {
    expect((ws({ 'logo.png': 'base64…' }).read('logo.png') as ReadError).error).toContain('binary');
  });

  it('slices to an inclusive 1-based range', () => {
    const r = ws({ 'a.ts': lines(10) }).read('a.ts', { startLine: 3, endLine: 5 });
    expect(r).toMatchObject({ content: 'x3\nx4\nx5', range: { start: 3, end: 5 } });
  });

  it('clamps an out-of-bounds range instead of failing', () => {
    // A stale line number from an earlier read should return nearby code, not an error the
    // model then has to recover from.
    const r = ws({ 'a.ts': lines(5) }).read('a.ts', { startLine: 4, endLine: 99 });
    expect(r).toMatchObject({ content: 'x4\nx5', range: { start: 4, end: 5 } });
  });

  it('clamps a start past the end of the file', () => {
    const r = ws({ 'a.ts': lines(5) }).read('a.ts', { startLine: 99 });
    expect(r).toMatchObject({ range: { start: 5, end: 5 }, content: 'x5' });
  });

  it('truncates a large whole-file read and says how to get the rest', () => {
    const r = ws({ 'big.ts': lines(MAX_WHOLE_FILE_LINES + 50) }).read('big.ts');
    expect(r).toMatchObject({ totalLines: MAX_WHOLE_FILE_LINES + 50 });
    expect((r as { content: string }).content).toContain('50 more lines');
    expect((r as { content: string }).content).toContain('startLine');
  });

  it('honours an explicit range even past the whole-file ceiling', () => {
    const r = ws({ 'big.ts': lines(MAX_WHOLE_FILE_LINES + 50) }).read('big.ts', {
      startLine: 401,
      endLine: 403,
    });
    expect(r).toMatchObject({ content: 'x401\nx402\nx403' });
  });

  it('skips the read when the caller already holds the hash', () => {
    const w = ws({ 'a.ts': 'same' });
    const hash = w.hashOf('a.ts')!;
    expect(w.read('a.ts', { ifHash: hash })).toEqual({ path: 'a.ts', hash, unchanged: true });
  });

  it('does not skip when the hash is stale', () => {
    const w = ws({ 'a.ts': 'changed' });
    expect(w.read('a.ts', { ifHash: 'old' })).toMatchObject({ content: 'changed' });
  });
});

describe('search', () => {
  const files = {
    'src/Hero.tsx': 'const Hero = () => <h1>hero</h1>;\nexport default Hero;',
    'src/Nav.tsx': 'export const Nav = () => null;',
    'styles/hero.css': '.hero { color: red }',
    'logo.png': 'aGVybw==',
  };

  it('returns compact path:line hits, case-insensitive by default', () => {
    const r = ws(files).search('hero');
    expect(r.hits.map((h) => `${h.path}:${h.line}`)).toEqual([
      'src/Hero.tsx:1',
      'src/Hero.tsx:2',
      'styles/hero.css:1',
    ]);
  });

  it('reports one hit per line, not per match', () => {
    // "Hero" appears twice on line 1; the model is choosing where to look, and saying so twice
    // costs twice as much for the same information.
    const r = ws({ 'a.ts': 'Hero and Hero again' }).search('Hero');
    expect(r.hits).toHaveLength(1);
  });

  it('skips binary files', () => {
    expect(
      ws(files)
        .search('hero')
        .hits.some((h) => h.path === 'logo.png'),
    ).toBe(false);
  });

  it('filters by path prefix', () => {
    const r = ws(files).search('hero', { paths: ['styles/'] });
    expect(r.hits.map((h) => h.path)).toEqual(['styles/hero.css']);
    expect(r.filesSearched).toBe(1);
  });

  it('honours case sensitivity', () => {
    expect(ws(files).search('Hero', { caseSensitive: true }).hits).toHaveLength(2);
  });

  it('treats the query literally unless regex is asked for', () => {
    const w = ws({ 'a.ts': 'a.b\naxb' });
    expect(w.search('a.b').hits).toHaveLength(1);
    expect(w.search('a.b', { regex: true }).hits).toHaveLength(2);
  });

  it('reports a bad regex instead of returning nothing', () => {
    const r = ws(files).search('(unclosed', { regex: true });
    expect(r.error).toBeDefined();
    expect(formatHits(r)).toContain('Invalid pattern');
  });

  it('caps results and says it stopped', () => {
    const r = ws({ 'a.ts': lines(DEFAULT_MAX_HITS + 10, 'match') }).search('match');
    expect(r.hits).toHaveLength(DEFAULT_MAX_HITS);
    expect(r.truncated).toBe(true);
    expect(formatHits(r)).toContain('narrow the query');
  });

  it('distinguishes "no matches" from "nothing searched"', () => {
    expect(formatHits(ws(files).search('zzz'))).toBe('No matches in 3 files.');
  });

  it('clips a long preview line', () => {
    const r = ws({ 'a.ts': `${'z'.repeat(400)}hit` }).search('hit');
    expect(r.hits[0].preview.length).toBeLessThan(200);
    expect(r.hits[0].preview.endsWith('…')).toBe(true);
  });
});

describe('search modes', () => {
  const files = {
    'src/Hero.tsx': 'export const Hero = () => <div/>;',
    'src/App.tsx': "import { Hero } from './Hero';\nconst page = <Hero />;\nexport default page;",
    'src/other.ts': 'const notHero = 1;',
  };
  const indexed = () => createWorkspace(files, META, buildIndex(files));

  it('symbol mode returns the definition, not every mention', () => {
    const r = indexed().search('Hero', { mode: 'symbol' });
    expect(r.hits).toEqual([{ path: 'src/Hero.tsx', line: 1, preview: 'export component Hero' }]);
  });

  it('symbol mode falls back to text when the scanner never saw the name', () => {
    // The scanner is a regex, not a parser, so an empty symbol result is not proof of absence.
    const r = indexed().search('notHero', { mode: 'symbol' });
    expect(r.hits.map((h) => h.path)).toEqual(['src/other.ts']);
  });

  it('references mode excludes the definition site', () => {
    const r = indexed().search('Hero', { mode: 'references' });
    expect(r.hits.some((h) => h.path === 'src/Hero.tsx' && h.line === 1)).toBe(false);
    expect(r.hits.some((h) => h.path === 'src/App.tsx')).toBe(true);
  });

  it('falls back to text search when no index was supplied', () => {
    const r = ws(files).search('Hero', { mode: 'symbol' });
    expect(r.hits.length).toBeGreaterThan(1);
  });

  it('caches a repeated search within one revision', () => {
    const w = indexed();
    const first = w.search('Hero');
    expect(w.search('Hero')).toBe(first);
  });

  it('does not confuse searches that differ only by option', () => {
    const w = indexed();
    expect(w.search('Hero', { mode: 'symbol' })).not.toBe(w.search('Hero', { mode: 'text' }));
    expect(w.search('Hero', { maxResults: 1 })).not.toBe(w.search('Hero', { maxResults: 2 }));
  });
});

describe('revision', () => {
  it('carries the pinned identity and fills hashes on demand', () => {
    const w = ws({ 'a.ts': 'x', 'b.ts': 'y' });
    expect(w.revision).toMatchObject({ siteId: 's1', branch: 'main', revision: 7 });
    expect(Object.keys(w.revision.hashes).sort()).toEqual(['a.ts', 'b.ts']);
  });

  it('is a snapshot: later store changes do not leak in', () => {
    const files: Record<string, string> = { 'a.ts': 'first' };
    const w = createWorkspace({ ...files }, META);
    files['a.ts'] = 'second';
    expect(w.read('a.ts')).toMatchObject({ content: 'first' });
  });
});
