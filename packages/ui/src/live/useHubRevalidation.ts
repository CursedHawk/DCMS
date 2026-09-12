import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useRef } from 'react';
import { useHubConnected } from './hubPresence';

/**
 * What replaces a poll.
 *
 * <p>Removing a <c>refetchInterval</c> is only correct if something else answers the question
 * the interval was really asking, which was never "has this changed?" but <b>"did I miss
 * anything?"</b>. The hub answers it while the socket is up. This hook answers it at the three
 * moments the socket cannot:</p>
 *
 * <ul>
 *   <li><b>Reconnect.</b> Nothing replays what was pushed while a connection was down.</li>
 *   <li><b>The tab becoming visible.</b> A backgrounded tab is where sockets get suspended and
 *       where a user is least likely to notice they are reading something old.</li>
 *   <li><b>The browser coming back online.</b> Earlier than the hub's own reconnect, and free.</li>
 * </ul>
 *
 * <p>Both apps set <c>refetchOnWindowFocus: false</c> globally and should keep it: refetching
 * every open query on every focus is exactly the load a console left on a second monitor must
 * not generate. This is the targeted version — named keys, and throttled.</p>
 *
 * <p><b>A known-down socket bypasses the throttle.</b> While the hub is connected, a refetch on
 * focus is insurance against a lost message and the throttle is the right guard. While it is
 * down, the data is unbacked by anything and the user has just come to look at it.</p>
 */

export interface HubRevalidationOptions {
  /** Which connection backs these keys — the name its hub hook reports under. */
  hub: string;
  /** Query key prefixes to invalidate. Same prefix matching react-query uses everywhere. */
  keys: readonly (readonly unknown[])[];
  /** Shortest gap between two focus-driven revalidations while the hub is up. */
  minIntervalMs?: number;
  enabled?: boolean;
}

/**
 * Half a minute. Long enough that alt-tabbing between two windows does not refetch on every
 * pass, short enough that a message lost while the socket was nominally up is not carried for
 * long.
 */
const DEFAULT_MIN_INTERVAL_MS = 30_000;

export function useHubRevalidation({
  hub,
  keys,
  minIntervalMs = DEFAULT_MIN_INTERVAL_MS,
  enabled = true,
}: HubRevalidationOptions): void {
  const queryClient = useQueryClient();
  const connected = useHubConnected(hub);

  // Read through refs inside the listeners so that a new `keys` array literal on every render
  // — which is what every call site will pass — cannot resubscribe the window listeners.
  const keysRef = useRef(keys);
  keysRef.current = keys;
  const minIntervalRef = useRef(minIntervalMs);
  minIntervalRef.current = minIntervalMs;
  const connectedRef = useRef(connected);
  connectedRef.current = connected;

  const lastRevalidated = useRef(0);

  const revalidate = useCallback(
    (force: boolean) => {
      const now = Date.now();
      if (!force && now - lastRevalidated.current < minIntervalRef.current) return;
      lastRevalidated.current = now;
      for (const queryKey of keysRef.current) {
        void queryClient.invalidateQueries({ queryKey });
      }
    },
    [queryClient],
  );

  // The socket came back. Everything pushed during the outage is gone, so this is forced.
  const wasConnected = useRef(connected);
  useEffect(() => {
    const previous = wasConnected.current;
    wasConnected.current = connected;
    if (!enabled) return;
    if (connected === true && previous === false) revalidate(true);
  }, [connected, enabled, revalidate]);

  useEffect(() => {
    if (!enabled) return;

    const onVisible = () => {
      if (document.visibilityState !== 'visible') return;
      revalidate(connectedRef.current === false);
    };
    const onOnline = () => revalidate(true);

    document.addEventListener('visibilitychange', onVisible);
    window.addEventListener('focus', onVisible);
    window.addEventListener('online', onOnline);
    return () => {
      document.removeEventListener('visibilitychange', onVisible);
      window.removeEventListener('focus', onVisible);
      window.removeEventListener('online', onOnline);
    };
  }, [enabled, revalidate]);
}
