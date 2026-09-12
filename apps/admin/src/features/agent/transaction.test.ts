import { describe, expect, it, vi } from 'vitest';
import { cachedHash } from './hash';
import { createTransaction, type FileChange, type WorkspacePort } from './transaction';

/** An in-memory port: the rules under test are the transaction's, not the VFS's. */
function memoryPort(initial: Record<string, string> = {}): WorkspacePort & {
  files: Record<string, string>;
} {
  const files = { ...initial };
  return {
    files,
    read: (p) => files[p],
    write: (p, c) => {
      files[p] = c;
    },
    remove: (p) => {
      delete files[p];
    },
    exists: (p) => files[p] !== undefined,
    // Mirrors the real normaliser's shape closely enough for these rules.
    normalize: (p) => (p && !p.includes('..') ? p.replace(/^\/+/, '') : null),
  };
}

describe('edit operations', () => {
  it('patches and reports the new hash so the next edit can guard on it', () => {
    const port = memoryPort({ 'a.ts': 'const a = 1;' });
    const tx = createTransaction(port);
    const r = tx.patch('a.ts', { oldText: '1', newText: '2' });
    expect(r.isError).toBeFalsy();
    expect(port.files['a.ts']).toBe('const a = 2;');
    expect(r.content).toContain(cachedHash('const a = 2;'));
  });

  it('honours a correct hash guard', () => {
    const port = memoryPort({ 'a.ts': 'x' });
    const tx = createTransaction(port);
    const r = tx.patch('a.ts', { oldText: 'x', newText: 'y', expectedHash: cachedHash('x') });
    expect(r.isError).toBeFalsy();
  });

  it('refuses a stale hash and names the current one', () => {
    const port = memoryPort({ 'a.ts': 'changed' });
    const tx = createTransaction(port);
    const r = tx.patch('a.ts', { oldText: 'changed', newText: 'y', expectedHash: 'stale' });
    expect(r.isError).toBe(true);
    expect(r.content).toContain(cachedHash('changed'));
    expect(r.content).toMatch(/Re-read it/);
    expect(port.files['a.ts']).toBe('changed');
  });

  it('reports a missing file rather than creating it', () => {
    const port = memoryPort();
    const r = createTransaction(port).patch('gone.ts', { oldText: 'a', newText: 'b' });
    expect(r.isError).toBe(true);
    expect(port.files['gone.ts']).toBeUndefined();
  });

  it('rejects a traversal path', () => {
    const r = createTransaction(memoryPort()).create('../escape.ts', 'x');
    expect(r.isError).toBe(true);
    expect(r.content).toContain('Invalid path');
  });

  it('replaces, inserts and deletes by line', () => {
    const port = memoryPort({ 'a.ts': 'one\ntwo\nthree' });
    const tx = createTransaction(port);
    expect(tx.replaceRange('a.ts', { startLine: 2, endLine: 2, text: 'TWO' }).isError).toBeFalsy();
    expect(port.files['a.ts']).toBe('one\nTWO\nthree');
    tx.insertAt('a.ts', { line: 1, text: 'zero' });
    expect(port.files['a.ts']).toBe('zero\none\nTWO\nthree');
    tx.deleteRange('a.ts', { startLine: 1, endLine: 1 });
    expect(port.files['a.ts']).toBe('one\nTWO\nthree');
  });
});

describe('create, remove, rename', () => {
  it('creates a new file', () => {
    const port = memoryPort();
    expect(createTransaction(port).create('new.ts', 'hi').isError).toBeFalsy();
    expect(port.files['new.ts']).toBe('hi');
  });

  it('refuses to create over an existing file', () => {
    // A create that silently overwrites is how a run destroys work nobody asked it to touch.
    const port = memoryPort({ 'a.ts': 'keep' });
    const r = createTransaction(port).create('a.ts', 'clobber');
    expect(r.isError).toBe(true);
    expect(port.files['a.ts']).toBe('keep');
  });

  it('renames, moving content', () => {
    const port = memoryPort({ 'a.ts': 'body' });
    expect(createTransaction(port).rename('a.ts', 'b.ts').isError).toBeFalsy();
    expect(port.files).toEqual({ 'b.ts': 'body' });
  });

  it('refuses a rename onto an existing file', () => {
    const port = memoryPort({ 'a.ts': '1', 'b.ts': '2' });
    expect(createTransaction(port).rename('a.ts', 'b.ts').isError).toBe(true);
    expect(port.files).toEqual({ 'a.ts': '1', 'b.ts': '2' });
  });

  it('refuses to delete a file that is not there', () => {
    expect(createTransaction(memoryPort()).remove('nope.ts').isError).toBe(true);
  });
});

describe('change set', () => {
  it('collapses repeated edits to one entry against the pre-run content', () => {
    const port = memoryPort({ 'a.ts': 'v1' });
    const tx = createTransaction(port);
    tx.patch('a.ts', { oldText: 'v1', newText: 'v2' });
    tx.patch('a.ts', { oldText: 'v2', newText: 'v3' });
    expect(tx.changes()).toEqual([{ path: 'a.ts', kind: 'modified', before: 'v1', after: 'v3' }]);
  });

  it('classifies creations and deletions', () => {
    const port = memoryPort({ 'gone.ts': 'x' });
    const tx = createTransaction(port);
    tx.create('made.ts', 'new');
    tx.remove('gone.ts');
    expect(tx.changes()).toEqual([
      { path: 'made.ts', kind: 'created', before: null, after: 'new' },
      { path: 'gone.ts', kind: 'deleted', before: 'x', after: null },
    ]);
  });

  it('omits a file edited back to where it started', () => {
    // Listing it would send the author to review an empty diff.
    const port = memoryPort({ 'a.ts': 'same' });
    const tx = createTransaction(port);
    tx.patch('a.ts', { oldText: 'same', newText: 'different' });
    tx.patch('a.ts', { oldText: 'different', newText: 'same' });
    expect(tx.changes()).toEqual([]);
  });

  it('preserves first-touched order', () => {
    const port = memoryPort({ 'b.ts': '1', 'a.ts': '1' });
    const tx = createTransaction(port);
    tx.patch('b.ts', { oldText: '1', newText: '2' });
    tx.patch('a.ts', { oldText: '1', newText: '2' });
    expect(tx.changes().map((c) => c.path)).toEqual(['b.ts', 'a.ts']);
  });

  it('notifies per applied edit', () => {
    const onChange = vi.fn<(c: FileChange) => void>();
    const tx = createTransaction(memoryPort({ 'a.ts': '1' }), { onChange });
    tx.patch('a.ts', { oldText: '1', newText: '2' });
    expect(onChange).toHaveBeenCalledWith(
      expect.objectContaining({ path: 'a.ts', kind: 'modified' }),
    );
  });

  it('does not notify for a refused edit', () => {
    const onChange = vi.fn();
    const tx = createTransaction(memoryPort({ 'a.ts': '1' }), { onChange });
    tx.patch('a.ts', { oldText: 'nope', newText: '2' });
    expect(onChange).not.toHaveBeenCalled();
  });
});

describe('revert', () => {
  it('restores the state before the run, not before the last edit', () => {
    const port = memoryPort({ 'a.ts': 'original' });
    const tx = createTransaction(port);
    tx.patch('a.ts', { oldText: 'original', newText: 'once' });
    tx.patch('a.ts', { oldText: 'once', newText: 'twice' });
    tx.revertAll();
    expect(port.files['a.ts']).toBe('original');
  });

  it('deletes a file the run created', () => {
    const port = memoryPort();
    const tx = createTransaction(port);
    tx.create('made.ts', 'x');
    tx.revertAll();
    expect(port.files['made.ts']).toBeUndefined();
  });

  it('restores a file the run deleted', () => {
    const port = memoryPort({ 'a.ts': 'body' });
    const tx = createTransaction(port);
    tx.remove('a.ts');
    tx.revertAll();
    expect(port.files['a.ts']).toBe('body');
  });

  it('undoes a rename completely', () => {
    const port = memoryPort({ 'a.ts': 'body' });
    const tx = createTransaction(port);
    tx.rename('a.ts', 'b.ts');
    tx.revertAll();
    expect(port.files).toEqual({ 'a.ts': 'body' });
  });

  it('reverts one file without touching the others', () => {
    const port = memoryPort({ 'a.ts': 'A', 'b.ts': 'B' });
    const tx = createTransaction(port);
    tx.patch('a.ts', { oldText: 'A', newText: 'A2' });
    tx.patch('b.ts', { oldText: 'B', newText: 'B2' });
    expect(tx.revert('a.ts')).toBe(true);
    expect(port.files).toEqual({ 'a.ts': 'A', 'b.ts': 'B2' });
  });

  it('reports nothing to revert for an untouched file', () => {
    expect(createTransaction(memoryPort({ 'a.ts': 'x' })).revert('a.ts')).toBe(false);
  });
});
