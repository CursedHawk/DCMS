import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { act, type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetHubPresence, setHubConnected } from './hubPresence';
import { useHubRevalidation } from './useHubRevalidation';

const HUB = 'test-hub';
const KEYS = [['alpha'], ['beta']] as const;

let queryClient: QueryClient;
let invalidate: ReturnType<typeof vi.fn>;

function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

function Probe({ enabled = true, minIntervalMs }: { enabled?: boolean; minIntervalMs?: number }) {
  // A fresh array literal on every render, which is what every real call site passes: if the
  // hook depended on its identity it would resubscribe its listeners constantly.
  useHubRevalidation({ hub: HUB, keys: [['alpha'], ['beta']], enabled, minIntervalMs });
  return null;
}

/** What the browser does when a tab comes back to the foreground. */
function becomeVisible(state: DocumentVisibilityState = 'visible') {
  Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
  act(() => {
    document.dispatchEvent(new Event('visibilitychange'));
  });
}

beforeEach(() => {
  vi.useFakeTimers();
  resetHubPresence();
  queryClient = new QueryClient();
  invalidate = vi.fn();
  queryClient.invalidateQueries = invalidate as unknown as QueryClient['invalidateQueries'];
});
afterEach(() => vi.useRealTimers());

describe('useHubRevalidation', () => {
  it('does nothing on mount', () => {
    // The queries mounted alongside it have just fetched. Refetching them again immediately is
    // the one moment there is definitely no news.
    render(<Probe />, { wrapper });
    expect(invalidate).not.toHaveBeenCalled();
  });

  it('refetches every key when the hub reconnects', () => {
    setHubConnected(HUB, true);
    render(<Probe />, { wrapper });
    act(() => setHubConnected(HUB, false));
    act(() => setHubConnected(HUB, true));

    expect(invalidate.mock.calls.map((c) => c[0].queryKey)).toEqual([...KEYS]);
  });

  it('does not refetch when the hub merely drops', () => {
    setHubConnected(HUB, true);
    render(<Probe />, { wrapper });
    act(() => setHubConnected(HUB, false));
    expect(invalidate).not.toHaveBeenCalled();
  });

  it('refetches when the tab becomes visible', () => {
    setHubConnected(HUB, true);
    render(<Probe />, { wrapper });
    becomeVisible();
    expect(invalidate).toHaveBeenCalledTimes(KEYS.length);
  });

  it('ignores the tab going away', () => {
    setHubConnected(HUB, true);
    render(<Probe />, { wrapper });
    becomeVisible('hidden');
    expect(invalidate).not.toHaveBeenCalled();
  });

  it('throttles repeated focus while the hub is up', () => {
    // Alt-tabbing between two windows must not refetch on every pass.
    setHubConnected(HUB, true);
    render(<Probe minIntervalMs={30_000} />, { wrapper });

    becomeVisible();
    becomeVisible();
    expect(invalidate).toHaveBeenCalledTimes(KEYS.length);

    vi.advanceTimersByTime(30_001);
    becomeVisible();
    expect(invalidate).toHaveBeenCalledTimes(KEYS.length * 2);
  });

  it('ignores the throttle while the hub is known to be down', () => {
    // Nothing is backing the data at all, and the user has just come to look at it.
    setHubConnected(HUB, false);
    render(<Probe minIntervalMs={30_000} />, { wrapper });

    becomeVisible();
    becomeVisible();
    expect(invalidate).toHaveBeenCalledTimes(KEYS.length * 2);
  });

  it('refetches when the browser comes back online', () => {
    setHubConnected(HUB, true);
    render(<Probe />, { wrapper });
    act(() => {
      window.dispatchEvent(new Event('online'));
    });
    expect(invalidate).toHaveBeenCalledTimes(KEYS.length);
  });

  it('does nothing at all when disabled', () => {
    setHubConnected(HUB, false);
    const { rerender } = render(<Probe enabled={false} />, { wrapper });
    becomeVisible();
    act(() => setHubConnected(HUB, true));
    rerender(<Probe enabled={false} />);
    expect(invalidate).not.toHaveBeenCalled();
  });

  it('stops listening once unmounted', () => {
    setHubConnected(HUB, false);
    const { unmount } = render(<Probe />, { wrapper });
    unmount();
    becomeVisible();
    expect(invalidate).not.toHaveBeenCalled();
  });
});
