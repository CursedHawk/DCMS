import type { HubConnection } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { createResourceInvalidator, setHubConnected, type ResourceChange } from '@dcms/ui';
import { useEffect, useRef, useState } from 'react';
import { getAccessToken } from '../../auth';
import { runtimeConfig } from '../../runtime-config';
import { LIVE_QUERY_MAP, LIVE_THROTTLE_MS } from './liveMap';

/**
 * Keeps the console current without it having to ask.
 *
 * <p>Mounted once, in the shell. One message arrives on this connection: `ResourceChanged`,
 * carrying the name of a class of data and nothing else. It invalidates the matching queries
 * and interrupts nobody — the refetch it causes is an ordinary authorised request, which is
 * what lets the server broadcast the tag to every open console without deciding who may see
 * what.</p>
 *
 * <p><b>The polls are gone, and this is what replaced them.</b> A hub is a hint and not a
 * guarantee — nothing replays what was pushed while a socket was down, and an operations
 * console that goes quietly stale during a NATS outage is exactly the wrong failure for the one
 * screen you open when things are wrong. What covers that is no longer a slow interval in every
 * list, but `useHubRevalidation` in the shell: a refetch when this connection comes back, when
 * the tab becomes visible, and when the browser comes back online. Those are the three moments
 * a message can have been missed, and between them nothing needs to be asked at all.</p>
 *
 * <p>The periodic reads that were never announcements — Prometheus, Loki, the platform totals —
 * did not simply lose their timer either. `PlatformSampleBroadcaster` runs it server-side and
 * pushes a tick, so those panels are as fresh as they were while the platform is read once per
 * replica instead of once per open tab.</p>
 */
/** The name this connection reports under, for `useHubRevalidation`. */
export const CONSOLE_HUB = 'console';

export function useConsoleHub(enabled: boolean) {
  const qc = useQueryClient();
  const [connected, setConnected] = useState(false);
  const connRef = useRef<HubConnection | null>(null);

  // Built once and read through a ref inside the socket handler, so rebuilding it can never
  // re-open the WebSocket.
  const invalidateRef = useRef(createResourceInvalidator(qc, LIVE_QUERY_MAP, LIVE_THROTTLE_MS));

  useEffect(() => {
    if (!enabled) return;

    let disposed = false;

    void (async () => {
      // Imported lazily: the shell mounts this on every page, and a static import would pull
      // the signalr chunk into the critical path of the first paint.
      const { HttpTransportType, HubConnectionBuilder, LogLevel } = await import('@microsoft/signalr');
      if (disposed) return;

      const connection = new HubConnectionBuilder()
        // skipNegotiation + WebSockets so the connection needs no server affinity: nothing at
        // the edge pins the negotiate POST and the transport connect to the same platform-api
        // replica. The Redis backplane fans messages across replicas; it says nothing about
        // which replica a single client's two handshake requests reach.
        .withUrl(`${runtimeConfig.platformApiBase}/hub/console`, {
          accessTokenFactory: async () => (await getAccessToken()) ?? '',
          skipNegotiation: true,
          transport: HttpTransportType.WebSockets,
        })
        .withAutomaticReconnect()
        .configureLogging(LogLevel.Warning)
        .build();

      connection.on('ResourceChanged', (change: ResourceChange) => invalidateRef.current(change));

      connection.onreconnected(() => {
        setConnected(true);
        setHubConnected(CONSOLE_HUB, true);
        // Everything pushed while the socket was down was missed and nothing replays it, so a
        // reconnect invalidates the whole cache: any page still open has been looking at data
        // that could have changed underneath it for the length of the outage.
        void qc.invalidateQueries();
      });
      connection.onclose(() => {
        setConnected(false);
        setHubConnected(CONSOLE_HUB, false);
      });

      try {
        await connection.start();
        if (disposed) {
          void connection.stop();
          return;
        }
        connRef.current = connection;
        setConnected(true);
        setHubConnected(CONSOLE_HUB, true);
      } catch (err) {
        setHubConnected(CONSOLE_HUB, false);
        // A console that cannot hold the socket is a slower console, not a broken one: every
        // list still loads and still polls. Log rather than toast, so a Redis or NATS outage
        // does not greet an operator with an error popup on the page they came to diagnose it.
        console.warn('Console hub connection failed.', err);
      }
    })();

    return () => {
      disposed = true;
      void connRef.current?.stop();
      connRef.current = null;
      setHubConnected(CONSOLE_HUB, false);
    };
  }, [enabled, qc]);

  return { connected };
}
