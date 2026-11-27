/**
 * Embeddable live-chat widget for published visitor sites. Vanilla TS (no React)
 * so it can be dropped into any prerendered Mode A page via a small bootstrap
 * script — `createChatWidget({ app: 'chat' })` — without a hydration runtime.
 *
 * Writes are plain requests to the AI Chatbot app's routes (`/api/{app}/chat/…`); what comes
 * back (the assistant's and agents' replies, the conversation closing) arrives on the app's
 * WebSockets channel `conversation/{id}` at `/hub/ws?instance={app}` (ADR 0037). The socket
 * speaks SignalR's JSON protocol directly, so the widget has no dependency.
 */

export interface ChatWidgetOptions {
  /** The AI Chatbot app's slug on this site (its `/api/{app}` routes). */
  app: string;
  /** Origin of the site's API and hub. Defaults to same-origin. */
  apiBase?: string;
  heading?: string;
  greeting?: string;
  visitorName?: string;
}

interface IncomingMessage {
  kind: 'message' | 'closed';
  id?: string;
  conversationId: string;
  sender?: 'Visitor' | 'Agent' | 'Bot';
  body?: string;
  sentAt?: string;
}

export interface ChatWidgetHandle {
  destroy: () => void;
}

const STORAGE_KEY = (app: string) => `dcms.chat.conversation.${app}`;
const RS = '\u001e';

export function createChatWidget(options: ChatWidgetOptions): ChatWidgetHandle {
  const heading = options.heading ?? 'Chat with us';
  const greeting = options.greeting ?? 'Hi! How can we help?';
  const base = options.apiBase ?? location.origin;
  const api = `${base}/api/${encodeURIComponent(options.app)}/chat/conversations`;

  const root = document.createElement('div');
  root.setAttribute('data-dcms-chat', '');
  root.innerHTML = renderShell(heading, greeting);
  document.body.appendChild(root);

  const log = root.querySelector('[data-log]') as HTMLDivElement;
  const input = root.querySelector('[data-input]') as HTMLInputElement;
  const sendBtn = root.querySelector('[data-send]') as HTMLButtonElement;
  const toggle = root.querySelector('[data-toggle]') as HTMLButtonElement;
  const panel = root.querySelector('[data-panel]') as HTMLDivElement;

  let conversationId = localStorage.getItem(STORAGE_KEY(options.app));
  let socket: WebSocket | null = null;
  let destroyed = false;
  let retry = 0;
  const seen = new Set<string>();

  function show(m: { id?: string; sender?: string; body?: string }) {
    if (m.id && seen.has(m.id)) return;
    if (m.id) seen.add(m.id);
    if (m.sender && m.sender !== 'Visitor') hideTyping(log);
    appendMessage(log, m.sender ?? 'Bot', m.body ?? '');
  }

  /** Listens on the conversation's channel; reconnects with backoff while the widget lives. */
  function listen(id: string) {
    socket?.close();
    const ws = new WebSocket(`${base.replace(/^http/, 'ws')}/hub/ws?instance=${encodeURIComponent(options.app)}`);
    socket = ws;
    ws.onopen = () => {
      ws.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
      ws.send(JSON.stringify({ type: 1, invocationId: '1', target: 'Subscribe', arguments: ['conversation', id.replace(/-/g, '')] }) + RS);
      retry = 0;
    };
    ws.onmessage = (e) => {
      for (const frame of String(e.data).split(RS)) {
        if (!frame) continue;
        const m = JSON.parse(frame) as { type?: number; target?: string; arguments?: unknown[] };
        if (m.type === 6) ws.send(JSON.stringify({ type: 6 }) + RS);
        if (m.type !== 1 || m.target !== 'message') continue;
        const payload = m.arguments?.[2] as IncomingMessage | undefined;
        if (payload?.kind === 'message') show(payload);
        if (payload?.kind === 'closed') forget();
      }
    };
    ws.onclose = () => {
      if (destroyed || socket !== ws || !conversationId) return;
      setTimeout(() => conversationId && !destroyed && listen(conversationId), [1000, 2000, 5000, 10000, 30000][Math.min(retry++, 4)]);
    };
  }

  function forget() {
    conversationId = null;
    localStorage.removeItem(STORAGE_KEY(options.app));
    socket?.close();
    socket = null;
    hideTyping(log);
  }

  async function ensureConversation(): Promise<string> {
    if (conversationId) return conversationId;
    const res = await fetch(api, {
      method: 'POST',
      credentials: 'include',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ visitorName: options.visitorName ?? null }),
    });
    if (!res.ok) throw new Error(`chat ${res.status}`);
    const id = ((await res.json()) as { id: string }).id;
    conversationId = id;
    localStorage.setItem(STORAGE_KEY(options.app), id);
    listen(id);
    return id;
  }

  async function send() {
    const body = input.value.trim();
    if (!body) return;
    const id = await ensureConversation();
    const res = await fetch(`${api}/${id}/messages`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ body }),
    });
    if (res.status === 404) {
      // Closed or gone: the next message opens a new conversation.
      forget();
      return;
    }
    if (!res.ok) return;
    show((await res.json()) as IncomingMessage);
    input.value = '';
    showTyping(log);
  }

  sendBtn.addEventListener('click', () => void send());
  input.addEventListener('keydown', (e) => {
    if ((e as KeyboardEvent).key === 'Enter') void send();
  });
  toggle.addEventListener('click', () => {
    panel.hidden = !panel.hidden;
  });

  // A returning visitor: replay the conversation, then follow it.
  if (conversationId) {
    const id = conversationId;
    void fetch(`${api}/${id}/messages`, { credentials: 'include' })
      .then(async (res) => {
        if (res.status === 404) return forget();
        if (!res.ok) return;
        for (const m of (await res.json()) as IncomingMessage[]) show(m);
        listen(id);
      })
      .catch(() => undefined);
  }

  return {
    destroy: () => {
      destroyed = true;
      socket?.close();
      root.remove();
    },
  };
}

let typingTimer: ReturnType<typeof setTimeout> | undefined;

function showTyping(log: HTMLElement) {
  if (log.querySelector('[data-typing]')) return;
  const row = document.createElement('div');
  row.setAttribute('data-typing', '');
  row.style.cssText = 'margin:4px 0;display:flex';
  const bubble = document.createElement('span');
  bubble.textContent = '…';
  bubble.style.cssText =
    'padding:6px 12px;border-radius:10px;font-size:16px;letter-spacing:2px;background:#f1f5f9;color:#64748b';
  row.appendChild(bubble);
  log.appendChild(row);
  log.scrollTop = log.scrollHeight;
  // Give up waiting if no reply arrives (AI unconfigured, error, or a human agent
  // will follow up later) so the dots never hang indefinitely.
  clearTimeout(typingTimer);
  typingTimer = setTimeout(() => hideTyping(log), 30_000);
}

function hideTyping(log: HTMLElement) {
  clearTimeout(typingTimer);
  log.querySelector('[data-typing]')?.remove();
}

function appendMessage(log: HTMLElement, sender: string, body: string) {
  const row = document.createElement('div');
  row.style.cssText = `margin:4px 0;display:flex;${sender === 'Visitor' ? 'justify-content:flex-end' : ''}`;
  const bubble = document.createElement('span');
  bubble.textContent = body;
  bubble.style.cssText = `max-width:80%;padding:6px 10px;border-radius:10px;font-size:14px;${
    sender === 'Visitor' ? 'background:#2563eb;color:#fff' : 'background:#f1f5f9;color:#0f172a'
  }`;
  row.appendChild(bubble);
  log.appendChild(row);
  log.scrollTop = log.scrollHeight;
}

function renderShell(heading: string, greeting: string): string {
  return `
    <button data-toggle aria-label="Open chat"
      style="position:fixed;bottom:20px;right:20px;width:56px;height:56px;border-radius:50%;border:0;background:#0f172a;color:#fff;font-size:24px;cursor:pointer;z-index:2147483000">💬</button>
    <div data-panel hidden
      style="position:fixed;bottom:88px;right:20px;width:320px;height:420px;background:#fff;border:1px solid #e2e8f0;border-radius:12px;display:flex;flex-direction:column;box-shadow:0 10px 30px rgba(0,0,0,.15);z-index:2147483000;font-family:system-ui,sans-serif">
      <div style="padding:10px 12px;border-bottom:1px solid #e2e8f0;font-weight:600">${escapeHtml(heading)}</div>
      <div data-log style="flex:1;overflow-y:auto;padding:10px">
        <div style="background:#f1f5f9;color:#0f172a;padding:6px 10px;border-radius:10px;font-size:14px;display:inline-block">${escapeHtml(greeting)}</div>
      </div>
      <div style="display:flex;gap:6px;padding:8px;border-top:1px solid #e2e8f0">
        <input data-input placeholder="Type a message…" style="flex:1;border:1px solid #cbd5e1;border-radius:6px;padding:6px 8px;font-size:14px" />
        <button data-send style="border:0;background:#0f172a;color:#fff;border-radius:6px;padding:6px 12px;cursor:pointer">Send</button>
      </div>
    </div>`;
}

function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c] as string,
  );
}
