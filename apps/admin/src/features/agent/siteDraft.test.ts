import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../../lib/api';
import type { Delta } from '../site-source/vfs';
import { useVfs } from '../site-source/vfs';
import { clientId } from '../site-source/clientId';
import { openSiteDraft } from './siteDraft';

/**
 * A run's own copy of its site (ADR 0033).
 *
 * <p>The HrSite conversation (2026-11-12) is what these pin down: a run that read App.tsx as it
 * was at run start while editing the current one, deleted lines by stale numbers and cut the file
 * in half; and a run orphaned by a site switch that wrote into whichever site the editor loaded
 * next.</p>
 */

function fakeApi(server: Record<string, string>) {
  const hashes = () => Object.fromEntries(Object.keys(server).map((p) => [p, `h:${server[p]}`]));
  const saves: { siteId: string; delta: Delta; origin: string; client: string }[] = [];
  return {
    saves,
    api: {
      load: vi.fn(async () => ({ branch: 'main', baseSha: null, version: 1, files: { ...server }, hashes: hashes() })),
      saveFiles: vi.fn(async (siteId: string, _branch: string, delta: Delta, origin = 'user', client = '') => {
        saves.push({ siteId, delta, origin, client });
        for (const [path, entry] of Object.entries(delta.put)) server[path] = entry.content;
        for (const { path } of delta.delete) delete server[path];
        return { version: 2, hashes: hashes() };
      }),
    },
  };
}

function editorHolds(siteId: string | null, files: Record<string, string>) {
  useVfs.setState({
    siteId,
    branch: 'main',
    loaded: true,
    files,
    baseHashes: Object.fromEntries(Object.keys(files).map((p) => [p, `h:${files[p]}`])),
    dirtyPaths: new Set(),
    deletedPaths: new Set(),
    conflict: null,
    dirty: false,
    version: 1,
    openTabs: [],
  });
}

beforeEach(() => {
  editorHolds(null, {});
});

describe('openSiteDraft', () => {
  it('reads its own writes — a fresh view per call, not a snapshot from run start', async () => {
    const { api } = fakeApi({ 'src/App.tsx': 'one\ntwo\nthree' });
    const draft = await openSiteDraft('site-a', { runId: 'r1', api });

    draft.port.write('src/App.tsx', 'one');
    const read = draft.workspace().read('src/App.tsx');
    expect('content' in read && read.content).toBe('one');
    expect('totalLines' in read && read.totalLines).toBe(1);
  });

  it('never touches the editor while the editor holds another site', async () => {
    // The orphaned-run bug: site A's agent writing into site B's store.
    editorHolds('site-b', { 'src/App.tsx': 'B' });
    const { api, saves } = fakeApi({ 'src/App.tsx': 'A' });
    const draft = await openSiteDraft('site-a', { runId: 'r1', api });

    draft.port.write('src/App.tsx', 'A2');
    await draft.flush();

    expect(useVfs.getState().files['src/App.tsx']).toBe('B');
    expect(saves).toHaveLength(1);
    expect(saves[0].siteId).toBe('site-a');
  });

  it('saves as the agent, under an id this tab recognises as its own', async () => {
    const { api, saves } = fakeApi({ 'a.ts': 'A' });
    const draft = await openSiteDraft('site-a', { runId: 'r1', api });
    draft.port.write('a.ts', 'A2');
    draft.port.remove('a.ts');
    draft.port.write('b.ts', 'B');
    await draft.flush();

    expect(saves).toHaveLength(1);
    expect(saves[0].origin).toBe('agent');
    expect(saves[0].client).toBe(`${clientId()}:agent:r1`);
    expect(saves[0].delta.put['b.ts'].content).toBe('B');
    expect(saves[0].delta.delete.map((d) => d.path)).toEqual(['a.ts']);
  });

  it('merges what it saved into the editor when the editor holds its site', async () => {
    editorHolds('site-a', { 'a.ts': 'A', 'b.ts': 'B' });
    const { api } = fakeApi({ 'a.ts': 'A', 'b.ts': 'B' });
    const draft = await openSiteDraft('site-a', { runId: 'r1', api });
    expect(api.load).not.toHaveBeenCalled();

    draft.port.write('a.ts', 'A2');
    await draft.flush();

    const vfs = useVfs.getState();
    expect(vfs.files['a.ts']).toBe('A2');
    // Already on the server, so not the author's autosave to send again.
    expect(vfs.dirty).toBe(false);
  });

  it("reads the author's latest text for files it is not itself writing", async () => {
    editorHolds('site-a', { 'a.ts': 'A', 'b.ts': 'B' });
    const { api } = fakeApi({ 'a.ts': 'A', 'b.ts': 'B' });
    const draft = await openSiteDraft('site-a', { runId: 'r1', api });

    useVfs.getState().writeFile('b.ts', 'typed by the author');
    expect(draft.files()['b.ts']).toBe('typed by the author');
  });

  it('takes the other version of a contested file and tells the model, keeping the rest pending', async () => {
    const { api } = fakeApi({ 'a.ts': 'A', 'b.ts': 'B' });
    const draft = await openSiteDraft('site-a', { runId: 'r1', api });
    api.saveFiles.mockRejectedValueOnce(new ApiError(409, 'conflict', { conflicts: [{ path: 'a.ts' }] }));
    api.load.mockResolvedValueOnce({
      branch: 'main',
      baseSha: null,
      version: 3,
      files: { 'a.ts': 'theirs', 'b.ts': 'B' },
      hashes: { 'a.ts': 'h:theirs', 'b.ts': 'h:B' },
    });

    draft.port.write('a.ts', 'mine');
    draft.port.write('b.ts', 'B2');
    await draft.flush();

    expect(draft.files()['a.ts']).toBe('theirs');
    expect(draft.takeNotes().join('\n')).toMatch(/a\.ts was NOT saved/);
    // b.ts was not contested: it goes with the next save.
    await draft.flush();
    expect(api.saveFiles).toHaveBeenLastCalledWith(
      'site-a',
      'main',
      expect.objectContaining({ put: { 'b.ts': { content: 'B2', baseHash: 'h:B' } } }),
      'agent',
      expect.any(String),
    );
  });
});
