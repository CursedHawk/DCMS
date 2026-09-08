import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useDebounced } from './useDebounced';

describe('useDebounced', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('returns the first value immediately', () => {
    const { result } = renderHook(() => useDebounced('news'));
    expect(result.current).toBe('news');
  });

  it('holds a change until the pause has passed', () => {
    const { result, rerender } = renderHook(({ v }) => useDebounced(v, 300), {
      initialProps: { v: '' },
    });

    rerender({ v: 'd' });
    expect(result.current).toBe('');

    act(() => void vi.advanceTimersByTime(299));
    expect(result.current).toBe('');

    act(() => void vi.advanceTimersByTime(1));
    expect(result.current).toBe('d');
  });

  /** The property the whole thing exists for: typing fast is one request, not one per letter. */
  it('reports only the last of a run of changes', () => {
    const { result, rerender } = renderHook(({ v }) => useDebounced(v, 300), {
      initialProps: { v: '' },
    });

    for (const v of ['d', 'do', 'doo', 'door', 'doors']) {
      rerender({ v });
      act(() => void vi.advanceTimersByTime(100));
    }

    expect(result.current).toBe('');

    act(() => void vi.advanceTimersByTime(300));
    expect(result.current).toBe('doors');
  });

  it('settles even when the value comes back to where it started', () => {
    const { result, rerender } = renderHook(({ v }) => useDebounced(v, 300), {
      initialProps: { v: 'news' },
    });

    rerender({ v: 'newsletter' });
    act(() => void vi.advanceTimersByTime(300));
    expect(result.current).toBe('newsletter');

    rerender({ v: 'news' });
    act(() => void vi.advanceTimersByTime(300));
    expect(result.current).toBe('news');
  });
});
