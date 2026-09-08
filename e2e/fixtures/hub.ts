import type { Page, WebSocketRoute } from '@playwright/test';

/** SignalR's record separator: every frame on the wire ends with it. */
const RS = '\u001e';

/**
 * A SignalR server, in the browser.
 *
 * <p>Both consoles open a hub with `skipNegotiation` and the WebSockets transport, so there is
 * exactly one socket and no HTTP negotiate to fake. Playwright's `routeWebSocket` answers it
 * without a server, which lets a spec do the thing the brief is actually about: have the
 * server say something and watch the page change without being reloaded.</p>
 *
 * <p>The JSON hub protocol is small enough to speak directly. The client opens with
 * `{"protocol":"json","version":1}`, the server answers `{}`, and everything after that is a
 * record-separated JSON message — an invocation being `{"type":1,"target":…,"arguments":[…]}`.
 * Keep-alive pings (`{"type":6}`) are answered in kind so `withAutomaticReconnect` never
 * decides the connection is dead mid-test.</p>
 */
export class HubMock {
  private route: WebSocketRoute | null = null;
  private handshaken = false;
  private readonly pending: string[] = [];
  private resolveConnected!: () => void;
  /** Resolves once the client has completed the SignalR handshake. */
  readonly connected: Promise<void>;

  constructor() {
    this.connected = new Promise<void>((resolve) => {
      this.resolveConnected = resolve;
    });
  }

  async install(page: Page, urlPattern: string): Promise<void> {
    await page.routeWebSocket(urlPattern, (ws) => {
      this.route = ws;
      ws.onMessage((message) => {
        const text = typeof message === 'string' ? message : message.toString('utf8');
        for (const frame of text.split(RS)) {
          if (!frame) continue;
          let parsed: { type?: number; protocol?: string };
          try {
            parsed = JSON.parse(frame) as { type?: number; protocol?: string };
          } catch {
            continue;
          }
          // The handshake carries `protocol` and no `type`.
          if (parsed.protocol) {
            ws.send(`{}${RS}`);
            this.handshaken = true;
            for (const queued of this.pending.splice(0)) ws.send(queued);
            this.resolveConnected();
            continue;
          }
          if (parsed.type === 6) ws.send(`{"type":6}${RS}`);
        }
      });
    });
  }

  /** Pushes a hub invocation the way admin-api and platform-api broadcast one. */
  send(target: string, ...args: unknown[]): void {
    const frame = `${JSON.stringify({ type: 1, target, arguments: args })}${RS}`;
    // A spec can push before the app has finished lazily importing @microsoft/signalr, and a
    // frame sent before the handshake response is a protocol error rather than an early
    // delivery. Hold it until the client has opened, or the test becomes a race.
    if (this.handshaken && this.route) this.route.send(frame);
    else this.pending.push(frame);
  }

  resourceChanged(tag: string, id?: string): void {
    this.send('ResourceChanged', { tag, id: id ?? null });
  }

  /**
   * Closes the socket the way a lost connection does.
   *
   * <p>`withAutomaticReconnect` then reopens it, `routeWebSocket` answers the new one, and the
   * client raises `onreconnected` — which is the path the consoles use to recover from an
   * outage. Nothing else in the suite exercises it, and it is the branch that decides whether
   * a console that was offline for a minute is showing stale data or fresh.</p>
   */
  async drop(): Promise<void> {
    this.handshaken = false;
    await this.route?.close({ code: 1006, reason: 'e2e: simulated outage' });
    this.route = null;
  }
}

/** The admin SPA's notification hub. */
export async function adminHub(page: Page): Promise<HubMock> {
  const hub = new HubMock();
  await hub.install(page, '**/api/hub/notifications**');
  return hub;
}

/** The platform console's hub. */
export async function platformHub(page: Page): Promise<HubMock> {
  const hub = new HubMock();
  await hub.install(page, '**/api/platform/hub/console**');
  return hub;
}
