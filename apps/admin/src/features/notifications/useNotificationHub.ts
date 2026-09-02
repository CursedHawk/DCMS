import type { HubConnection } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from '@tanstack/react-router';
import { toast } from 'sonner';
import { getAccessToken } from '../../auth';
import { runtimeConfig } from '../../runtime-config';
import { getCurrentTenantSlug } from '../../tenants';
import { type Notification, notificationsKey, parseParams } from './api';

/**
 * Opens the notification hub for the signed-in user and keeps the bell live.
 *
 * Mounted once, in the app shell. Everything it receives lands in the react-query cache under
 * {@link notificationsKey}, so the bell itself stays a plain consumer of that cache and needs
 * no knowledge of the socket.
 */
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

      connection.onreconnected(() => {
        setConnected(true);
        // Anything raised while the socket was down never arrived; refetch rather than
        // leaving a stale badge until the next navigation.
        void qc.invalidateQueries({ queryKey: notificationsKey() });
      });
      connection.onclose(() => setConnected(false));

      try {
        await connection.start();
        if (disposed) {
          void connection.stop();
          return;
        }
        connRef.current = connection;
        setConnected(true);
      } catch (err) {
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
  navigate: (opts: { to: string }) => void,
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
      ? { label: t('notifications.view'), onClick: () => navigate({ to: n.linkPath as string }) }
      : undefined,
  };

  if (n.severity === 'Error') toast.error(title, options);
  else if (n.severity === 'Warning') toast.warning(title, options);
  else if (n.severity === 'Success') toast.success(title, options);
  else toast(title, options);
}
