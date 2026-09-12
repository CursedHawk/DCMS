import { beforeEach, describe, expect, it } from 'vitest';
import { useVfs } from './vfs';

/**
 * The rules that decide whether the author gets interrupted.
 *
 * <p>Every case below used to end the same way — a banner offering a reload — because the old
 * code could not tell "somebody saved a file you have never opened" from "somebody changed the
 * line you are typing on". These tests pin the distinction.</p>
 */

const hash = (s: string) => `h:${s}`;
const hashesOf = (files: Record<string, string>) =>
  Object.fromEntries(Object.entries(files).map(([p, c]) => [p, hash(c)]));

function loadServer(files: Record<string, string>, version = 1) {
  useVfs.getState().load(files, version, hashesOf(files));
}

beforeEach(() => {
  useVfs.setState({
    files: {},
    baseHashes: {},
    dirtyPaths: new Set(),
    deletedPaths: new Set(),
    conflict: null,
    dirty: false,
    version: 0,
    agentRuns: 0,
    openTabs: [],
    activePath: null,
  });
});

describe('mergeRemote', () => {
  it('adopts a file this tab never touched, silently', () => {
    loadServer({ 'a.ts': 'A', 'b.ts': 'B' });
    const diverged = useVfs
      .getState()
      .mergeRemote({ 'a.ts': 'A', 'b.ts': 'B2' }, 2, hashesOf({ 'a.ts': 'A', 'b.ts': 'B2' }));

    expect(diverged).toEqual([]);
    expect(useVfs.getState().files['b.ts']).toBe('B2');
    expect(useVfs.getState().conflict).toBeNull();
  });

  it('leaves a locally-edited file alone when the server changed a different one', () => {
    // The common case by a wide margin, and the one that used to produce a reload prompt.
    loadServer({ 'a.ts': 'A', 'b.ts': 'B' });
    useVfs.getState().writeFile('a.ts', 'my edit');

    const remote = { 'a.ts': 'A', 'b.ts': 'B2' };
    const diverged = useVfs.getState().mergeRemote(remote, 2, hashesOf(remote));

    expect(diverged).toEqual([]);
    expect(useVfs.getState().files['a.ts']).toBe('my edit');
    expect(useVfs.getState().files['b.ts']).toBe('B2');
    expect(useVfs.getState().dirtyPaths.has('a.ts')).toBe(true);
  });

  it('reconciles when both sides reached the same text', () => {
    // Not a conflict by any useful definition — typically this tab's own save arriving back.
    loadServer({ 'a.ts': 'A' });
    useVfs.getState().writeFile('a.ts', 'same');

    const remote = { 'a.ts': 'same' };
    expect(useVfs.getState().mergeRemote(remote, 2, hashesOf(remote))).toEqual([]);
    expect(useVfs.getState().dirtyPaths.has('a.ts')).toBe(false);
    expect(useVfs.getState().baseHashes['a.ts']).toBe(hash('same'));
  });

  it('reports only genuine divergence', () => {
    loadServer({ 'a.ts': 'A' });
    useVfs.getState().writeFile('a.ts', 'mine');

    const remote = { 'a.ts': 'theirs' };
    expect(useVfs.getState().mergeRemote(remote, 2, hashesOf(remote))).toEqual(['a.ts']);
    expect(useVfs.getState().conflict).toEqual(['a.ts']);
    // The local text is kept: the resolver needs both sides, and discarding the author's work
    // to show them a conflict would be the bug the conflict UI exists to prevent.
    expect(useVfs.getState().files['a.ts']).toBe('mine');
  });

  it('adopts a server-side deletion of an untouched file', () => {
    loadServer({ 'a.ts': 'A', 'gone.ts': 'G' });
    useVfs.getState().mergeRemote({ 'a.ts': 'A' }, 2, hashesOf({ 'a.ts': 'A' }));
    expect(useVfs.getState().files['gone.ts']).toBeUndefined();
  });

  it('keeps a locally-edited file the server deleted', () => {
    loadServer({ 'a.ts': 'A' });
    useVfs.getState().writeFile('a.ts', 'still mine');
    useVfs.getState().mergeRemote({}, 2, {});
    expect(useVfs.getState().files['a.ts']).toBe('still mine');
  });

  it('advances the version and does not bump the generation', () => {
    // A generation bump resyncs every Monaco model, discarding cursors and undo history.
    loadServer({ 'a.ts': 'A' });
    const before = useVfs.getState().generation;
    useVfs.getState().mergeRemote({ 'a.ts': 'A2' }, 7, hashesOf({ 'a.ts': 'A2' }));
    expect(useVfs.getState().version).toBe(7);
    expect(useVfs.getState().generation).toBe(before);
  });

  it('does not flag an unsaved local edit when the server has not moved', () => {
    // The single most common state in the editor: something typed, not yet flushed. Comparing
    // local text to server text would call this a conflict every time.
    loadServer({ 'a.ts': 'A' });
    useVfs.getState().writeFile('a.ts', 'typing…');

    const remote = { 'a.ts': 'A' };
    expect(useVfs.getState().mergeRemote(remote, 2, hashesOf(remote))).toEqual([]);
    expect(useVfs.getState().files['a.ts']).toBe('typing…');
    expect(useVfs.getState().dirtyPaths.has('a.ts')).toBe(true);
  });

  it('accumulates conflicts across successive merges', () => {
    loadServer({ 'a.ts': 'A', 'b.ts': 'B' });
    useVfs.getState().writeFile('a.ts', 'mine-a');
    useVfs.getState().writeFile('b.ts', 'mine-b');

    const first = { 'a.ts': 'x', 'b.ts': 'mine-b' };
    useVfs.getState().mergeRemote(first, 2, hashesOf(first));
    expect(useVfs.getState().conflict).toEqual(['a.ts']);

    // b.ts was reconciled by the first merge (both sides matched), so re-dirty it to model the
    // author editing it again before the server moves underneath.
    useVfs.getState().writeFile('b.ts', 'mine-b-again');
    const second = { 'a.ts': 'x', 'b.ts': 'y' };
    useVfs.getState().mergeRemote(second, 3, hashesOf(second));

    expect(useVfs.getState().conflict?.slice().sort()).toEqual(['a.ts', 'b.ts']);
  });
});

describe('conflict quarantine', () => {
  it('excludes only the contested paths from the delta', () => {
    loadServer({ 'a.ts': 'A', 'b.ts': 'B' });
    const vfs = useVfs.getState();
    vfs.writeFile('a.ts', 'A2');
    vfs.writeFile('b.ts', 'B2');
    useVfs.getState().setConflict(['a.ts']);

    const delta = useVfs.getState().takeDelta();
    expect(Object.keys(delta.put)).toEqual(['b.ts']);
  });

  it('excludes a contested deletion too', () => {
    loadServer({ 'a.ts': 'A', 'b.ts': 'B' });
    useVfs.getState().deleteFile('a.ts');
    useVfs.getState().deleteFile('b.ts');
    useVfs.getState().setConflict(['a.ts']);

    expect(
      useVfs
        .getState()
        .takeDelta()
        .delete.map((d) => d.path),
    ).toEqual(['b.ts']);
  });

  it('unions successive conflict reports rather than replacing them', () => {
    // Replacing would release the first file from quarantine and let the next flush clobber it.
    useVfs.getState().setConflict(['a.ts']);
    useVfs.getState().setConflict(['b.ts']);
    expect(useVfs.getState().conflict?.sort()).toEqual(['a.ts', 'b.ts']);
  });

  it('resolving a file returns it to the save flow against the server baseline', () => {
    loadServer({ 'a.ts': 'A', 'b.ts': 'B' });
    useVfs.getState().writeFile('a.ts', 'mine');
    useVfs.getState().setConflict(['a.ts']);

    useVfs.getState().resolveConflict('a.ts', 'merged', hash('theirs'));

    expect(useVfs.getState().conflict).toBeNull();
    expect(useVfs.getState().files['a.ts']).toBe('merged');
    // Against the server's hash, or the very next save 409s again on the same file.
    expect(useVfs.getState().baseHashes['a.ts']).toBe(hash('theirs'));
    expect(Object.keys(useVfs.getState().takeDelta().put)).toContain('a.ts');
  });

  it('keeps the other conflicts when one is resolved', () => {
    useVfs.getState().setConflict(['a.ts', 'b.ts']);
    useVfs.getState().resolveConflict('a.ts', 'x', 'h');
    expect(useVfs.getState().conflict).toEqual(['b.ts']);
  });
});

describe('agent run hold', () => {
  it('counts nested runs and floors at zero', () => {
    const vfs = useVfs.getState();
    vfs.beginAgentRun();
    vfs.beginAgentRun();
    expect(useVfs.getState().agentRuns).toBe(2);
    useVfs.getState().endAgentRun();
    expect(useVfs.getState().agentRuns).toBe(1);
    useVfs.getState().endAgentRun();
    useVfs.getState().endAgentRun();
    // An unmatched end must not go negative, which would hold autosave forever.
    expect(useVfs.getState().agentRuns).toBe(0);
  });
});
