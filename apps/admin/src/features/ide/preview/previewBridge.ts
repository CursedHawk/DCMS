/**
 * A channel into the running preview, so the agent can see what the page actually does.
 *
 * <p>This is the difference between an agent that writes plausible React and one that can tell
 * whether the page works. A build that compiles can still throw on mount, fetch a 404, or render
 * an element three viewports wide — none of which the compiler knows and all of which the
 * browser already does.</p>
 *
 * <h3>Why a message channel and not contentDocument</h3>
 * <p>A `srcdoc` iframe inherits the parent's origin, so reaching into `contentDocument` would
 * work today. It would also mean the agent's tools hold a live DOM reference into a document
 * that is replaced on every rebuild, and that any stray query runs synchronously on the parent's
 * thread. A message channel keeps the boundary explicit and survives the iframe being swapped.</p>
 *
 * <h3>What is collected, and what is not</h3>
 * <p>Console errors and warnings, uncaught exceptions, failed promises and failed network
 * requests. Not `console.log`: a React app in development is chatty, and a debug line the author
 * left in is not a signal the agent should be reasoning about — or paying for.</p>
 */

export interface PreviewMessage {
  kind: 'error' | 'warn' | 'exception' | 'rejection' | 'network';
  text: string;
  at: number;
}

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
  var fetchOriginal = window.fetch;
  window.fetch = function (input, init) {
    return fetchOriginal.apply(this, arguments).then(function (res) {
      if (!res.ok) post('network', res.status + ' ' + (typeof input === 'string' ? input : (input && input.url) || ''));
      return res;
    }, function (err) {
      post('network', 'failed ' + (typeof input === 'string' ? input : '') + ' — ' + (err && err.message));
      throw err;
    });
  };
  window.addEventListener('message', function (ev) {
    var data = ev.data;
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
export function attachPreview(target: Window | null): () => void {
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
    const data = event.data as
      { __dcms?: string; kind?: PreviewMessage['kind']; text?: string; id?: number } | undefined;
    if (!data || typeof data !== 'object') return;

    if (data.__dcms === 'preview' && data.kind) {
      messages.push({ kind: data.kind, text: data.text ?? '', at: Date.now() });
      if (messages.length > MAX_MESSAGES) messages = messages.slice(-MAX_MESSAGES);
      return;
    }
    if (data.__dcms === 'reply' && typeof data.id === 'number') {
      pending.get(data.id)?.(data.text ?? '');
      pending.delete(data.id);
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
