/**
 * DCMS analytics for a Mode B (React) site.
 *
 * This is the same collector the prerendered Mode A runtime ships
 * (`hydrate.js`), speaking the identical wire contract — one beacon shape, one
 * consent key, one `/api/collect` endpoint — so a tenant's numbers mean the same
 * thing whichever way their site is built. Two things genuinely differ, and they
 * are the reason this file exists rather than a `<script>` tag:
 *
 * 1. **A SPA does not reload the document.** A React router changes the URL
 *    without a navigation, so a per-document pageview beacon records the entry
 *    page and nothing else. This patches the history API and listens for
 *    `popstate`, which is the only way to see a client-side route change.
 * 2. **A static React bundle has no publish-time hook.** A Mode A page is
 *    stamped with the tenant's analytics state by the assembler; this has to ask
 *    (`GET /api/analytics/status`) so it knows whether there is anything to seek
 *    consent *for*.
 *
 * **Nothing is stored and nothing is sent before consent.** `send()` is the only
 * exit, and it gates on `allowed()` — so declining does not merely hide the
 * banner, it stops the session id from ever being created.
 *
 * ---
 * This file is part of the DCMS-generated layer (`src/dcms/`, beside `src/api/`).
 * The "Refresh API" button in the editor overwrites it, so local edits are lost.
 * Everything worth changing is a parameter of `installAnalytics()`.
 */

/** Answers persist across visits — being re-asked every visit is what makes banners hated. */
const CONSENT_KEY = 'dcms-consent';
/** Per-tab, so the backend can derive an anonymous visitor without a cookie. */
const SESSION_KEY = 'dcms-sid';

export type ConsentState = 'granted' | 'denied' | 'unknown';

export interface AnalyticsOptions {
  /**
   * Where the DCMS API lives. Empty means same-origin, which is the normal case:
   * a published site is served from the tenant's own domain and the API is
   * proxied under `/api`. Set it only when the site is hosted elsewhere.
   */
  apiBaseUrl?: string;
  /**
   * `banner` (default) asks first and records nothing until the visitor accepts.
   * `off` records immediately — only appropriate where the owner has established
   * they do not need consent.
   */
  mode?: 'banner' | 'off';
  /** Track clicks on links leaving the site, and on file downloads. */
  trackOutbound?: boolean;
  /** Send how long the visitor stayed, once, when the page is first hidden. */
  trackEngagement?: boolean;
}

interface Resolved extends Required<AnalyticsOptions> {}

const defaults: Resolved = {
  apiBaseUrl: '',
  mode: 'banner',
  trackOutbound: true,
  trackEngagement: true,
};

let options: Resolved = defaults;
/** null until `/api/analytics/status` answers. Unknown means "do not send". */
let recording: boolean | null = null;
let installed = false;
const listeners = new Set<() => void>();

/* -------------------------------------------------------------- consent -- */

export function consentState(): ConsentState {
  try {
    const stored = localStorage.getItem(CONSENT_KEY);
    return stored === 'granted' || stored === 'denied' ? stored : 'unknown';
  } catch {
    // Storage blocked entirely (private mode, cookies off). Treat it as a
    // refusal: an answer cannot be recorded, so one must not be assumed.
    return 'denied';
  }
}

export function setConsent(value: 'granted' | 'denied'): void {
  try {
    localStorage.setItem(CONSENT_KEY, value);
  } catch {
    /* the in-memory decision still governs this page */
  }
  notify();
  if (value === 'granted') sendPageview();
}

/** Whether a banner should be shown: only when there is something to consent to. */
export function consentNeeded(): boolean {
  return recording === true && options.mode !== 'off' && consentState() === 'unknown';
}

function allowed(): boolean {
  if (recording !== true) return false;
  return options.mode === 'off' || consentState() === 'granted';
}

/** Subscribe to consent/status changes — how the banner knows to appear or go. */
export function onAnalyticsChange(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function notify(): void {
  for (const listener of listeners) listener();
}

/* -------------------------------------------------------------- sending -- */

function sessionId(): string | null {
  try {
    let value = sessionStorage.getItem(SESSION_KEY);
    if (!value) {
      value = Date.now().toString(36) + Math.random().toString(36).slice(2, 10);
      sessionStorage.setItem(SESSION_KEY, value);
    }
    return value;
  } catch {
    return null;
  }
}

/** The path INCLUDING its query — that is where `utm_*` attribution lives. */
function currentPath(): string {
  return `${location.pathname || '/'}${location.search || ''}`;
}

function send(type: string, path: string, props?: Record<string, unknown>): void {
  // The single gate. Nothing leaves the page — and no session id is created —
  // without consent.
  if (!allowed()) return;
  try {
    void fetch(`${options.apiBaseUrl}/api/collect`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        type,
        path,
        referrer: document.referrer || null,
        sessionId: sessionId(),
        props: props ?? null,
      }),
      credentials: 'same-origin',
      // The page may be unloading (pageleave, outbound click); without this the
      // browser cancels the request as it tears the document down.
      keepalive: true,
    }).catch(() => {});
  } catch {
    /* best effort — analytics must never break a page */
  }
}

export function sendPageview(): void {
  send('pageview', currentPath());
}

/** Record something of your own: `trackEvent('signup', { plan: 'pro' })`. */
export function trackEvent(type: string, props?: Record<string, unknown>): void {
  send(type, currentPath(), props);
}

/* ------------------------------------------------------------- tracking -- */

/**
 * Client-side route changes.
 *
 * `pushState`/`replaceState` fire no event, so they are wrapped. The beacon is
 * deferred to a microtask because a router updates the URL *before* it renders,
 * and a pageview is more useful attributed to the page that actually appeared.
 */
function trackRouteChanges(): void {
  let last = currentPath();
  const fire = () => {
    queueMicrotask(() => {
      const now = currentPath();
      if (now === last) return; // a replaceState that changed nothing
      last = now;
      sendPageview();
    });
  };

  for (const name of ['pushState', 'replaceState'] as const) {
    const original = history[name];
    history[name] = function patched(this: History, ...args: Parameters<History['pushState']>) {
      const result = original.apply(this, args);
      fire();
      return result;
    };
  }
  addEventListener('popstate', fire);
}

const DOWNLOAD_EXT = /\.(pdf|zip|rar|7z|gz|tar|docx?|xlsx?|pptx?|csv|txt|rtf|dmg|exe|pkg|apk|mp3|mp4|wav|mov)$/i;

/** Outbound links and downloads: what a visitor does that a pageview cannot see. */
function trackOutboundClicks(): void {
  document.addEventListener(
    'click',
    (event) => {
      const anchor = (event.target as Element | null)?.closest?.('a[href]');
      if (!anchor) return;
      const href = anchor.getAttribute('href') ?? '';
      if (!href || href.startsWith('#') || href.startsWith('javascript:')) return;

      let url: URL;
      try {
        url = new URL(href, location.href);
      } catch {
        return;
      }
      if (url.protocol !== 'http:' && url.protocol !== 'https:') return;

      if (url.host !== location.host) send('outbound', currentPath(), { url: url.href });
      else if (DOWNLOAD_EXT.test(url.pathname)) send('download', currentPath(), { file: url.pathname });
    },
    true,
  );
}

/**
 * How long the visitor stayed. Sent once, on the first time the page is hidden —
 * `visibilitychange` is the only signal that fires reliably on mobile, where
 * `unload` often does not.
 */
function trackEngagement(): void {
  const start = Date.now();
  let sent = false;
  document.addEventListener('visibilitychange', () => {
    if (sent || document.visibilityState !== 'hidden') return;
    sent = true;
    send('pageleave', currentPath(), { seconds: Math.round((Date.now() - start) / 1000) });
  });
}

/* --------------------------------------------------------------- install -- */

/**
 * Start collecting. Safe to call once, at app startup; calling it again is a no-op.
 *
 * Listeners are attached immediately but stay inert until consent is given —
 * attaching them later would miss the clicks a visitor makes while deciding, and
 * `send()` refuses anyway, so nothing escapes early.
 */
export function installAnalytics(opts: AnalyticsOptions = {}): void {
  if (installed || typeof window === 'undefined') return;
  installed = true;
  options = { ...defaults, ...opts };

  trackRouteChanges();
  if (options.trackOutbound) trackOutboundClicks();
  if (options.trackEngagement) trackEngagement();

  // Ask whether this tenant records anything. Until it answers, `recording` is
  // null: no beacon, and no banner for a site that may have nothing to ask about.
  void fetch(`${options.apiBaseUrl}/api/analytics/status`, {
    headers: { Accept: 'application/json' },
    credentials: 'same-origin',
  })
    .then((res) => (res.ok ? res.json() : { enabled: false }))
    .then((body: { enabled?: boolean }) => {
      recording = body.enabled === true;
      notify();
      if (allowed()) sendPageview();
    })
    .catch(() => {
      // Unreachable API: record nothing and ask nothing. Failing closed is the
      // only safe direction for something that stores data about people.
      recording = false;
      notify();
    });
}
