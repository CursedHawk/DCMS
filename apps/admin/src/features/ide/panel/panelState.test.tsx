import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { usePanelState } from './panelState';

/**
 * The bottom panel's open/closed behaviour.
 *
 * <p>The rule worth pinning down is the one every editor with a bottom panel shares and nobody
 * documents: clicking the tab you are already on closes the panel. Get it wrong and there is no
 * way to dismiss the thing except the chevron, which most people never find.</p>
 */

beforeEach(() => localStorage.clear());

describe('usePanelState', () => {
  it('starts closed on Problems', () => {
    const { result } = renderHook(() => usePanelState());
    expect(result.current).toMatchObject({ open: false, tab: 'problems' });
  });

  it('opens on the tab that was clicked', () => {
    const { result } = renderHook(() => usePanelState());
    act(() => result.current.toggle('console'));
    expect(result.current).toMatchObject({ open: true, tab: 'console' });
  });

  it('closes when the active tab is clicked again', () => {
    const { result } = renderHook(() => usePanelState());
    act(() => result.current.toggle('console'));
    act(() => result.current.toggle('console'));
    expect(result.current.open).toBe(false);
  });

  it('switches tab rather than closing when a different one is clicked', () => {
    const { result } = renderHook(() => usePanelState());
    act(() => result.current.toggle('console'));
    act(() => result.current.toggle('build'));
    expect(result.current).toMatchObject({ open: true, tab: 'build' });
  });

  it('reopens on the remembered tab', () => {
    const { result } = renderHook(() => usePanelState());
    act(() => result.current.toggle('output'));
    act(() => result.current.close());
    act(() => result.current.toggle());
    expect(result.current).toMatchObject({ open: true, tab: 'output' });
  });

  it('always opens for show(), even on the tab already showing', () => {
    // `show` is what a *command* calls — "show me the problems" must never be the thing that
    // hides them.
    const { result } = renderHook(() => usePanelState());
    act(() => result.current.toggle('problems'));
    act(() => result.current.show('problems'));
    expect(result.current.open).toBe(true);
  });

  it('remembers across a remount', () => {
    const first = renderHook(() => usePanelState());
    act(() => first.result.current.toggle('build'));
    first.unmount();

    const second = renderHook(() => usePanelState());
    expect(second.result.current).toMatchObject({ open: true, tab: 'build' });
  });

  it('ignores a stored tab it does not recognise', () => {
    // An older or newer build wrote it. Falling back beats rendering nothing.
    localStorage.setItem('dcms.ide.panel.tab', 'terminal');
    const { result } = renderHook(() => usePanelState());
    expect(result.current.tab).toBe('problems');
  });
});
