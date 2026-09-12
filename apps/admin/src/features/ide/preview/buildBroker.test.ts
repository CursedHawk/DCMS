import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  latestBuild,
  previewAvailable,
  publishBuildResult,
  registerPreview,
  requestBuild,
  resetBroker,
} from './buildBroker';

beforeEach(resetBroker);

describe('availability', () => {
  it('reports unavailable with no preview attached', () => {
    expect(previewAvailable()).toBe(false);
  });

  it('reports available once a pane registers, and not after it unregisters', () => {
    const off = registerPreview(() => {});
    expect(previewAvailable()).toBe(true);
    off();
    expect(previewAvailable()).toBe(false);
  });

  it('returns null rather than hanging when nothing can build', async () => {
    // The honest answer to "does it compile" with no compiler attached is "I cannot check".
    await expect(requestBuild(1)).resolves.toBeNull();
  });
});

describe('requesting a build', () => {
  it('triggers a refresh and resolves with the published result', async () => {
    const refresh = vi.fn(() => publishBuildResult(true, [], 5));
    registerPreview(refresh);

    const snapshot = await requestBuild(5);
    expect(refresh).toHaveBeenCalledOnce();
    expect(snapshot).toMatchObject({ ok: true, revision: 5 });
  });

  it('serves the cache when it already reflects the current revision', async () => {
    // Re-bundling to learn what is already known is seconds of WASM for nothing.
    const refresh = vi.fn(() => publishBuildResult(true, [], 5));
    registerPreview(refresh);
    await requestBuild(5);

    const again = await requestBuild(5);
    expect(refresh).toHaveBeenCalledOnce();
    expect(again).toMatchObject({ revision: 5 });
  });

  it('rebuilds when the revision has moved on', async () => {
    let rev = 5;
    const refresh = vi.fn(() => publishBuildResult(true, [], rev));
    registerPreview(refresh);
    await requestBuild(5);

    rev = 6;
    await requestBuild(6);
    expect(refresh).toHaveBeenCalledTimes(2);
  });

  it('carries problems through', async () => {
    registerPreview(() =>
      publishBuildResult(false, [{ severity: 'error', text: 'boom', file: 'a.ts', line: 3 }], 2),
    );
    const snapshot = await requestBuild(2);
    expect(snapshot).toMatchObject({ ok: false, problems: [{ text: 'boom' }] });
  });

  it('times out rather than waiting forever on a dead worker', async () => {
    vi.useFakeTimers();
    registerPreview(() => {
      /* never publishes */
    });
    const pending = requestBuild(1, 100);
    await vi.advanceTimersByTimeAsync(150);
    await expect(pending).resolves.toBeNull();
    vi.useRealTimers();
  });

  it('releases a waiter when the pane unregisters mid-build', async () => {
    const off = registerPreview(() => {});
    const pending = requestBuild(1);
    off();
    // Signalled as "could not build" (revision -1) rather than left hanging until the timeout.
    await expect(pending).resolves.toMatchObject({ revision: -1 });
  });
});

describe('latestBuild', () => {
  it('is null before anything has built', () => {
    expect(latestBuild()).toBeNull();
  });

  it('clears when a new pane registers', () => {
    // A freshly mounted pane has built nothing, and the previous one's result may describe a
    // different site entirely.
    registerPreview(() => {});
    publishBuildResult(true, [], 1);
    expect(latestBuild()).not.toBeNull();

    registerPreview(() => {});
    expect(latestBuild()).toBeNull();
  });
});
