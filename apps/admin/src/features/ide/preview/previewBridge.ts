/**
 * A channel into the running preview, so the agent can see what the page actually does.
 *
 * <p>This is the difference between an agent that writes plausible React and one that can tell
 * whether the page works. A build that compiles can still throw on mount, fetch a 404, or render
 * an element three viewports wide — none of which the compiler knows and all of which the
 * browser already does.</p>
 *
 * <h3>Why a message channel and not contentDocument</h3>
 * <p>The preview iframe runs in an opaque origin (its sandbox deliberately omits
 * `allow-same-origin`, so tenant- and CDN-authored code cannot read this admin origin's tokens
 * or script the parent — SEC-10). `contentDocument` is therefore cross-origin and unreadable,
 * and a message channel is not an optimisation but the only channel there is. It also survives
 * the iframe being swapped on every rebuild, where a held DOM reference would go stale.</p>
 *
 * <h3>What is collected, and what is not</h3>
 * <p>Console errors and warnings, uncaught exceptions, failed promises and failed network
 * requests. Not `console.log`: a React app in development is chatty, and a debug line the author
 * left in is not a signal the agent should be reasoning about — or paying for.</p>
 *
 * <h3>The API proxy</h3>
 * <p>Because the iframe is opaque-origin it cannot reach the DCMS API itself: a relative
 * `/api/...` has no origin to resolve against, and it holds no admin token for the `site:edit`
 * gated preview endpoint. So the injected shim forwards those requests to this parent over the
 * same channel; the parent replays them with the admin session (see {@link PreviewFetchProxy})
 * and only ever for paths inside the site's own preview subtree, which is what lets the live
 * preview show real tenant content without ever handing the sandbox a credential.</p>
 */

export interface PreviewMessage {
  kind: 'error' | 'warn' | 'exception' | 'rejection' | 'network';
  text: string;
  at: number;
}

/** One API request the sandboxed preview asks the parent to make on its behalf. */
export interface PreviewFetchRequest {
  /** The path the site fetched, always an absolute `/api/...` path (never a foreign origin). */
  url: string;
  method: string;
  headers: Record<string, string>;
  /** A string body only (JSON); the site's content/analytics/form calls never stream. */
  body: string | null;
}

/** The parent's answer, marshalled back across the channel into a real {@link Response}. */
export interface PreviewFetchResult {
  status: number;
  statusText: string;
  headers: Record<string, string>;
  body: string;
}

/**
 * Replays a preview request against the DCMS API with the admin session. Supplied by the
 * pane, because only it has the token and knows which site's preview subtree is in bounds —
 * this module stays free of app auth. A handler MUST reject any path outside that subtree
 * (return status 403) so preview code cannot borrow the admin token for another endpoint.
 */
export type PreviewFetchProxy = (req: PreviewFetchRequest) => Promise<PreviewFetchResult>;

/** Ring buffer size. A page in a render loop can produce thousands; the newest ones are what matter. */
const MAX_MESSAGES = 100;

let messages: PreviewMessage[] = [];
let frame: Window | null = null;
/** Removes the currently-installed listener, if any. See the note in {@link attachPreview}. */
let detach: (() => void) | null = null;
let requestSeq = 0;
const pending = new Map<number, (value: string) => void>();

/** The script injected into every preview document. Must be self-contained and never throw. */
export const BRIDGE_SCRIPT = `
(function () {
  var post = function (kind, text) {
    try { parent.postMessage({ __dcms: 'preview', kind: kind, text: String(text).slice(0, 2000) }, '*'); } catch (e) {}
  };
  var wrap = function (name, kind) {
    var original = console[name];
    console[name] = function () {
      try { post(kind, Array.prototype.map.call(arguments, String).join(' ')); } catch (e) {}
      return original.apply(console, arguments);
    };
  };
  wrap('error', 'error');
  wrap('warn', 'warn');
  window.addEventListener('error', function (e) {
    post('exception', (e.message || 'Error') + (e.filename ? ' at ' + e.filename + ':' + e.lineno : ''));
  });
  window.addEventListener('unhandledrejection', function (e) {
    post('rejection', (e.reason && (e.reason.message || e.reason)) || 'Unhandled rejection');
  });
  // API requests are proxied through the parent: the iframe is opaque-origin, so a relative
  // /api/... has no origin to resolve and it holds no admin token. Everything else (a CDN font,
  // an absolute URL) goes straight to the network. See the "API proxy" note in previewBridge.ts.
  var proxySeq = 0;
  var proxyPending = {};
  var urlOf = function (input) {
    return typeof input === 'string' ? input : (input && input.url) || '';
  };
  var headersOf = function (init, input) {
    var out = {};
    try {
      var h = (init && init.headers) || (input && input.headers);
      if (!h) return out;
      if (typeof h.forEach === 'function' && !Array.isArray(h)) {
        h.forEach(function (v, k) { out[k] = v; });
      } else if (Array.isArray(h)) {
        h.forEach(function (p) { out[p[0]] = p[1]; });
      } else {
        Object.keys(h).forEach(function (k) { out[k] = h[k]; });
      }
    } catch (e) {}
    return out;
  };
  var fetchOriginal = window.fetch;
  window.fetch = function (input, init) {
    var u = urlOf(input);
    // Same-origin API paths only. A tenant site never legitimately fetches another absolute path
    // from the opaque origin, and the parent enforces the exact preview subtree besides.
    if (typeof u === 'string' && u.indexOf('/api/') === 0) {
      var method = (init && init.method) || (input && input.method) || 'GET';
      var body = init && typeof init.body === 'string' ? init.body : null;
      var headers = headersOf(init, input);
      return new Promise(function (resolve, reject) {
        var id = ++proxySeq;
        var timer = setTimeout(function () {
          if (proxyPending[id]) {
            delete proxyPending[id];
            post('network', 'timeout ' + u);
            reject(new TypeError('Failed to fetch'));
          }
        }, 20000);
        proxyPending[id] = function (d) {
          clearTimeout(timer);
          var status = d.status || 0;
          if (!status) {
            post('network', 'failed ' + u + ' — preview proxy error');
            reject(new TypeError('Failed to fetch'));
            return;
          }
          var noBody = status === 204 || status === 205 || status === 304;
          var res = new Response(noBody ? null : (d.body || ''), {
            status: status,
            statusText: d.statusText || '',
            headers: d.headers || {},
          });
          if (!res.ok) post('network', status + ' ' + u);
          resolve(res);
        };
        try {
          parent.postMessage(
            { __dcms: 'proxy-fetch', id: id, url: u, method: method, headers: headers, body: body },
            '*',
          );
        } catch (e) {
          clearTimeout(timer);
          delete proxyPending[id];
          reject(e);
        }
      });
    }
    return fetchOriginal.apply(this, arguments).then(function (res) {
      if (!res.ok) post('network', res.status + ' ' + u);
      return res;
    }, function (err) {
      post('network', 'failed ' + u + ' — ' + (err && err.message));
      throw err;
    });
  };
  window.addEventListener('message', function (ev) {
    // Only our host (the parent that injected this) may answer a proxy request or ask a query.
    // A real message always carries its source, so this keeps another frame from feeding the
    // preview forged API responses or driving its query surface.
    if (ev.source && ev.source !== parent) return;
    var data = ev.data;
    if (data && data.__dcms === 'proxy-reply' && proxyPending[data.id]) {
      var cb = proxyPending[data.id];
      delete proxyPending[data.id];
      cb(data);
      return;
    }
    if (!data || data.__dcms !== 'query') return;
    var reply = function (text) {
      parent.postMessage({ __dcms: 'reply', id: data.id, text: String(text).slice(0, 20000) }, '*');
    };
    try {
      if (data.op === 'dom') {
        var el = data.selector ? document.querySelector(data.selector) : document.body;
        reply(el ? el.outerHTML : 'No element matches ' + data.selector);
      } else if (data.op === 'text') {
        var t = data.selector ? document.querySelector(data.selector) : document.body;
        reply(t ? (t.innerText || t.textContent || '') : 'No element matches ' + data.selector);
      } else {
        reply('Unknown query: ' + data.op);
      }
    } catch (e) {
      reply('Query failed: ' + (e && e.message));
    }
  });
})();
`;

/**
 * Begin listening. Called by the preview pane; returns the teardown.
 *
 * <p>The buffer is cleared on every registration because a rebuild produces a new document, and
 * errors from the previous version of the code are worse than no errors — the agent would try to
 * fix something it has already fixed.</p>
 */
export function attachPreview(target: Window | null, proxy?: PreviewFetchProxy): () => void {
  // Detach whatever was listening first.
  //
  // The caller is expected to run the teardown, and in the app React does. But a re-attach
  // without one is easy to write and its symptom is subtle rather than loud: the old listener
  // stays registered and every console message is recorded twice, so the agent reads a page as
  // having twice the errors it has. Making attach idempotent removes the trap.
  detach?.();

  frame = target;
  messages = [];

  const onMessage = (event: MessageEvent) => {
    // Only the attached preview iframe may drive the bridge. The preview is opaque-origin, so
    // event.origin is the string "null" and cannot single it out — but its window identity can.
    // A real cross-window postMessage always carries its source, so pinning to `frame` keeps any
    // other embedded frame or stray script from spoofing console output or, far worse, triggering
    // an authenticated proxy-fetch. (A null source only arises for synthetic events — our own
    // tests — and a same-window sender, neither of which can smuggle the admin response anywhere.)
    if (event.source && event.source !== frame) return;

    const data = event.data as
      {
        __dcms?: string;
        kind?: PreviewMessage['kind'];
        text?: string;
        id?: number;
        url?: string;
        method?: string;
        headers?: Record<string, string>;
        body?: string | null;
      } | undefined;
    if (!data || typeof data !== 'object') return;

    if (data.__dcms === 'preview' && data.kind) {
      messages.push({ kind: data.kind, text: data.text ?? '', at: Date.now() });
      if (messages.length > MAX_MESSAGES) messages = messages.slice(-MAX_MESSAGES);
      return;
    }
    if (data.__dcms === 'reply' && typeof data.id === 'number') {
      pending.get(data.id)?.(data.text ?? '');
      pending.delete(data.id);
      return;
    }
    // The sandboxed preview asking us to make an API call it cannot make itself. Only honoured
    // when a proxy is wired and the reply goes back to the same iframe; the proxy decides what
    // is in bounds. `frame` is captured so a rebuild that swaps the iframe mid-flight replies to
    // the window that asked, not whatever is on screen when the answer returns.
    if (data.__dcms === 'proxy-fetch' && typeof data.id === 'number' && proxy) {
      const id = data.id;
      const source = frame;
      const req: PreviewFetchRequest = {
        url: typeof data.url === 'string' ? data.url : '',
        method: typeof data.method === 'string' ? data.method : 'GET',
        headers: data.headers ?? {},
        body: typeof data.body === 'string' ? data.body : null,
      };
      const answer = (result: PreviewFetchResult) =>
        source?.postMessage({ __dcms: 'proxy-reply', id, ...result }, '*');
      void proxy(req).then(answer, () =>
        answer({ status: 0, statusText: 'proxy error', headers: {}, body: '' }),
      );
    }
  };

  window.addEventListener('message', onMessage);
  const teardown = () => {
    window.removeEventListener('message', onMessage);
    if (detach === teardown) detach = null;
    if (frame === target) frame = null;
  };
  detach = teardown;
  return teardown;
}

/** Drop buffered messages — called when a rebuild starts, so old errors do not look current. */
export function clearPreviewMessages(): void {
  messages = [];
}

export function previewMessages(): readonly PreviewMessage[] {
  return messages;
}

export function previewAttached(): boolean {
  return frame !== null;
}

/**
 * Ask the running page a question.
 *
 * <p>Resolves null when nothing is attached or the page does not answer — a page stuck in a
 * render loop will not service a message, and waiting forever is worse than saying so.</p>
 */
export async function queryPreview(
  op: 'dom' | 'text',
  selector?: string,
  timeoutMs = 3000,
): Promise<string | null> {
  if (!frame) return null;
  const id = ++requestSeq;
  const answer = new Promise<string>((resolve) => pending.set(id, resolve));
  frame.postMessage({ __dcms: 'query', id, op, selector }, '*');

  const timeout = new Promise<null>((resolve) =>
    setTimeout(() => {
      pending.delete(id);
      resolve(null);
    }, timeoutMs),
  );
  return Promise.race([answer, timeout]);
}

/** Test seam. */
export function resetBridge(): void {
  detach?.();
  messages = [];
  frame = null;
  pending.clear();
}
