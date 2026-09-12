import type { HubConnection } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { createResourceInvalidator, setHubConnected, type ResourceChange } from '@dcms/ui';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from '@tanstack/react-router';
import { toast } from 'sonner';
import { getAccessToken } from '../../auth';
import { runtimeConfig } from '../../runtime-config';
import { getCurrentTenantSlug } from '../../tenants';
import { type Notification, notificationsKey, parseParams } from './api';
import { LIVE_QUERY_MAP } from './liveMap';
import { linkTarget } from './linkPath';

/**
 * Opens the hub for the signed-in user, and keeps both the bell and every open page current.
 *
 * <p>Mounted once, in the app shell. Two things arrive on this one connection:</p>
 *
 * <p><b>`Notification`</b> — addressed to this user, stored, and possibly worth a toast.
 * It lands in the react-query cache under {@link notificationsKey}, so the bell stays a plain
 * consumer of that cache and needs no knowledge of the socket.</p>
 *
 * <p><b>`ResourceChanged`</b> — addressed to every console open on the tenant, carrying only
 * the name of a class of data. It invalidates the matching queries and interrupts nobody.
 * This is what replaced the polling: the media grid used to refetch every three seconds while
 * anything was processing, the chat list every fifteen, and the deployments view every two
 * and a half after a publish, all of them guessing at when the server might have news.</p>
 *
 * <p>The last of those intervals is now gone too. What covers a message missed while the socket
 * was down is `useHubRevalidation`, mounted in the shell alongside this hook: nothing replays a
 * push, so the honest answer is to refetch at the moments a gap can have opened — reconnect,
 * the tab becoming visible, and coming back online — rather than to keep asking in case.</p>
 */
/** The name this connection reports under, for `useHubRevalidation`. */
export const NOTIFICATION_HUB = 'notifications';

export function useNotificationHub(enabled: boolean, myUserId: string | undefined) {
  const qc = useQueryClient();
  const navigate = useNavigate();
  const { t } = useTranslation();
  const [connected, setConnected] = useState(false);
  const connRef = useRef<HubConnection | null>(null);

  // Read through refs inside the socket handler: re-creating the connection whenever the
  // route or the translation function changes would drop and re-open the WebSocket on every
  // navigation.
  const myUserIdRef = useRef(myUserId);
  myUserIdRef.current = myUserId;
  const navigateRef = useRef(navigate);
  navigateRef.current = navigate;
  const tRef = useRef(t);
  tRef.current = t;

  const slug = getCurrentTenantSlug();

  // Built once per client, and read through a ref inside the socket handler for the same
  // reason as `t` and `navigate`: rebuilding it must not re-open the WebSocket.
  const invalidateRef = useRef(createResourceInvalidator(qc, LIVE_QUERY_MAP));
  const invalidate = (change: ResourceChange) => invalidateRef.current(change);

  useEffect(() => {
    if (!enabled || !slug) return;

    let disposed = false;

    void (async () => {
      // Imported lazily: this hook mounts in the shell on every page, and a static import
      // would pull the ~55 KB signalr chunk into the critical path of the first paint.
      const { HttpTransportType, HubConnectionBuilder, LogLevel } = await import('@microsoft/signalr');
      if (disposed) return;

      const connection = new HubConnectionBuilder()
        // skipNegotiation + WebSockets so the connection needs no server affinity: nothing at
        // the edge pins the negotiate POST and the transport connect to the same admin-api
        // replica, so a scaled deployment would drop connections at random. The Redis
        // backplane fans messages out across replicas; it says nothing about which replica a
        // single client's two handshake requests reach.
        .withUrl(`${runtimeConfig.adminApiBase}/hub/notifications?tenant=${encodeURIComponent(slug)}`, {
          accessTokenFactory: async () => (await getAccessToken()) ?? '',
          skipNegotiation: true,
          transport: HttpTransportType.WebSockets,
        })
        .withAutomaticReconnect()
        .configureLogging(LogLevel.Warning)
        .build();

      connection.on('Notification', (n: Notification) => {
        void qc.invalidateQueries({ queryKey: notificationsKey() });
        maybeToast(n, myUserIdRef.current, tRef.current, navigateRef.current);
      });

      connection.on('ResourceChanged', (change: ResourceChange) => invalidate(change));

      connection.onreconnected(() => {
        setConnected(true);
        setHubConnected(NOTIFICATION_HUB, true);
        /*
         * Everything pushed while the socket was down was missed, and nothing replays it —
         * the hub is push-only and holds no backlog. So a reconnect invalidates the whole
         * cache rather than only the bell: any page still open has been looking at data that
         * could have changed underneath it for the length of the outage.
         */
        void qc.invalidateQueries();
      });
      connection.onclose(() => {
        setConnected(false);
        setHubConnected(NOTIFICATION_HUB, false);
      });

      try {
        await connection.start();
        if (disposed) {
          void connection.stop();
          return;
        }
        connRef.current = connection;
        setConnected(true);
        setHubConnected(NOTIFICATION_HUB, true);
      } catch (err) {
        setHubConnected(NOTIFICATION_HUB, false);
        // A bell that cannot connect is a degraded bell, not a broken page: the list still
        // loads over REST. Log rather than toast, so a NATS/Redis outage does not greet
        // every admin with an error popup.
        console.warn('Notification hub connection failed.', err);
      }
    })();

    return () => {
      disposed = true;
      void connRef.current?.stop();
      connRef.current = null;
      setHubConnected(NOTIFICATION_HUB, false);
    };
  }, [enabled, slug, qc]);

  return { connected };
}

/**
 * The toast policy: always interrupt for Warning and Error, but for Info and Success only
 * when somebody else caused it. Without the second clause, publishing ten pages toasts ten
 * times at the person who clicked publish. The notification still reaches the bell either
 * way — this decides only whether it also interrupts.
 */
function maybeToast(
  n: Notification,
  myUserId: string | undefined,
  t: (key: string, options?: Record<string, unknown>) => string,
  navigate: (opts: ReturnType<typeof linkTarget>) => void,
) {
  const isSelfInflicted = !!myUserId && n.actorUserId === myUserId;
  const important = n.severity === 'Warning' || n.severity === 'Error';
  if (!important && isSelfInflicted) return;

  const params = parseParams(n.paramsJson);
  const title = t(n.titleKey, params);
  const body = t(n.bodyKey, params);

  const options = {
    description: body,
    action: n.linkPath
      ? { label: t('notifications.view'), onClick: () => navigate(linkTarget(n.linkPath as string)) }
      : undefined,
  };

  if (n.severity === 'Error') toast.error(title, options);
  else if (n.severity === 'Warning') toast.warning(title, options);
  else if (n.severity === 'Success') toast.success(title, options);
  else toast(title, options);
}
