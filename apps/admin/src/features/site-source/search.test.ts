import { describe, expect, it } from 'vitest';
import { groupByFile, MAX_MATCHES, searchFiles, type SearchOptions } from './search';

const plain: SearchOptions = { caseSensitive: false, wholeWord: false, useRegex: false };

const files = {
  'src/App.tsx': 'export function App() {\n  return <h1>Hello</h1>;\n}\n',
  'src/main.tsx': "import { App } from './App';\ncreateRoot(el).render(<App />);\n",
  'README.md': '# hello world\n',
};

describe('searchFiles', () => {
  it('finds a match and says where it is, ready for the editor', () => {
    const { matches } = searchFiles(files, 'Hello', { ...plain, caseSensitive: true });

    expect(matches).toHaveLength(1);
    expect(matches[0]).toMatchObject({
      path: 'src/App.tsx',
      line: 2,
      column: 14,
      start: 13,
      end: 18,
    });
    expect(matches[0].lineText).toBe('  return <h1>Hello</h1>;');
  });

  it('is case-insensitive unless told otherwise', () => {
    expect(searchFiles(files, 'hello', plain).matches).toHaveLength(2);
    expect(searchFiles(files, 'hello', { ...plain, caseSensitive: true }).matches).toHaveLength(1);
  });

  it('finds every match on one line', () => {
    const { matches } = searchFiles({ 'a.ts': 'App App App' }, 'App', plain);
    expect(matches.map((m) => m.column)).toEqual([1, 5, 9]);
  });

  it('treats the query as text, not as a pattern', () => {
    const { matches } = searchFiles({ 'a.ts': 'a.b\naxb' }, 'a.b', plain);
    expect(matches.map((m) => m.line)).toEqual([1]);
  });

  it('takes a regular expression when asked', () => {
    const { matches } = searchFiles({ 'a.ts': 'a.b\naxb' }, 'a.b', { ...plain, useRegex: true });
    expect(matches.map((m) => m.line)).toEqual([1, 2]);
  });

  /** The normal state of the box while somebody is typing one. */
  it('reports a regular expression that will not compile instead of finding nothing', () => {
    const result = searchFiles(files, '(unclosed', { ...plain, useRegex: true });
    expect(result.error).toBeTruthy();
    expect(result.matches).toEqual([]);
  });

  it('matches whole words only when asked', () => {
    const source = { 'a.ts': 'App\nApplication' };
    expect(searchFiles(source, 'App', plain).matches).toHaveLength(2);
    expect(searchFiles(source, 'App', { ...plain, wholeWord: true }).matches).toHaveLength(1);
  });

  /** A pattern that matches nothing at all would otherwise never advance past one line. */
  it('terminates on a pattern that can match the empty string', () => {
    const { matches } = searchFiles({ 'a.ts': 'ab' }, 'x*', { ...plain, useRegex: true });
    expect(matches.length).toBeGreaterThan(0);
    expect(matches.length).toBeLessThan(10);
  });

  it('skips binary files rather than searching their base64', () => {
    const withImage = { 'logo.png': 'AAAAdeadbeefAAAA', 'a.ts': 'deadbeef' };
    expect(searchFiles(withImage, 'deadbeef', plain).matches.map((m) => m.path)).toEqual(['a.ts']);
  });

  it('says when it stopped counting', () => {
    const line = 'x\n'.repeat(MAX_MATCHES + 50);
    const result = searchFiles({ 'a.ts': line }, 'x', plain);

    expect(result.truncated).toBe(true);
    expect(result.matches).toHaveLength(MAX_MATCHES);
  });

  it('finds nothing for an empty query, rather than everything', () => {
    expect(searchFiles(files, '', plain).matches).toEqual([]);
  });
});

describe('groupByFile', () => {
  it('keeps each file together and in the order they were searched', () => {
    const { matches } = searchFiles(files, 'app', plain);
    const groups = groupByFile(matches);

    expect(groups.map((g) => g.path)).toEqual(['src/App.tsx', 'src/main.tsx']);
    expect(groups[1].matches.length).toBeGreaterThan(1);
  });
});
