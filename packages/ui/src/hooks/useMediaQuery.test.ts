import { act, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useMediaQuery } from './useMediaQuery';

/**
 * A stub that reports its CURRENT width rather than the one it was created with.
 *
 * <p>The obvious version captures `matches` when `matchMedia` is called, and every test that
 * changes the viewport then passes or fails for the wrong reason — the hook could stop
 * listening entirely and the test would not notice.</p>
 */
function stubViewport(initiallyWide: boolean) {
  let wide = initiallyWide;
  const listeners = new Set<() => void>();

  vi.stubGlobal('matchMedia', (query: string) => ({
    get matches() {
      return wide;
    },
    media: query,
    addEventListener: (_: string, fn: () => void) => void listeners.add(fn),
    removeEventListener: (_: string, fn: () => void) => void listeners.delete(fn),
  }));

  return {
    resize(next: boolean) {
      wide = next;
      act(() => listeners.forEach((fn) => fn()));
    },
    get listenerCount() {
      return listeners.size;
    },
  };
}

afterEach(() => vi.unstubAllGlobals());

describe('useMediaQuery', () => {
  it('reports a query that already matches, without waiting for a resize', () => {
    stubViewport(true);
    const { result } = renderHook(() => useMediaQuery('(min-width: 1024px)'));
    expect(result.current).toBe(true);
  });

  it('follows the viewport', () => {
    const viewport = stubViewport(false);
    const { result } = renderHook(() => useMediaQuery('(min-width: 1024px)'));

    expect(result.current).toBe(false);
    viewport.resize(true);
    expect(result.current).toBe(true);
    viewport.resize(false);
    expect(result.current).toBe(false);
  });

  it('stops listening when it goes away', () => {
    const viewport = stubViewport(true);
    const { unmount } = renderHook(() => useMediaQuery('(min-width: 1024px)'));

    expect(viewport.listenerCount).toBe(1);
    unmount();
    expect(viewport.listenerCount).toBe(0);
  });

  /** jsdom without the stub, and every non-browser environment: no match, no throw. */
  it('reports no match where matchMedia does not exist', () => {
    vi.stubGlobal('matchMedia', undefined);
    const { result } = renderHook(() => useMediaQuery('(min-width: 1024px)'));
    expect(result.current).toBe(false);
  });
});
