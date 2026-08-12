/**
 * Embeddable live-chat widget for published visitor sites. Vanilla TS (no React)
 * so it can be dropped into any prerendered Mode A page via a small bootstrap
 * script — `createChatWidget({ tenant: 'acme' })` — without a hydration runtime.
 *
 * It connects to content-api's SignalR hub through the site-host proxy
 * (`/hub/chat`), starts/persists a conversation in localStorage, and renders a
 * minimal floating panel. Realtime fan-out (including the agent's replies) is
 * delivered by the hub's per-conversation group.
 */
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import type { HubConnection } from '@microsoft/signalr';

export interface ChatWidgetOptions {
  /** Tenant slug, passed to the hub's ?tenant= query. */
  tenant: string;
  /** Hub origin. Defaults to same-origin (site-host proxies /hub to content-api). */
  hubBase?: string;
  heading?: string;
  greeting?: string;
  visitorName?: string;
}

interface IncomingMessage {
  id: string;
  conversationId: string;
  sender: 'Visitor' | 'Agent' | 'Bot';
  body: string;
  sentAt: string;
}

export interface ChatWidgetHandle {
  destroy: () => void;
}

const STORAGE_KEY = (tenant: string) => `dcms.chat.conversation.${tenant}`;

export function createChatWidget(options: ChatWidgetOptions): ChatWidgetHandle {
  const heading = options.heading ?? 'Chat with us';
  const greeting = options.greeting ?? 'Hi! How can we help?';
  const hubBase = options.hubBase ?? '';

  const root = document.createElement('div');
  root.setAttribute('data-dcms-chat', '');
  root.innerHTML = renderShell(heading, greeting);
  document.body.appendChild(root);

  const log = root.querySelector('[data-log]') as HTMLDivElement;
  const input = root.querySelector('[data-input]') as HTMLInputElement;
  const sendBtn = root.querySelector('[data-send]') as HTMLButtonElement;
  const toggle = root.querySelector('[data-toggle]') as HTMLButtonElement;
  const panel = root.querySelector('[data-panel]') as HTMLDivElement;

  let conversationId = localStorage.getItem(STORAGE_KEY(options.tenant));

  const connection: HubConnection = new HubConnectionBuilder()
    .withUrl(`${hubBase}/hub/chat?tenant=${encodeURIComponent(options.tenant)}`)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Error)
    .build();

  connection.on('ReceiveMessage', (m: IncomingMessage) => {
    if (m.conversationId === conversationId) {
      // A reply (assistant or agent) ends the "typing…" state.
      if (m.sender !== 'Visitor') hideTyping(log);
      appendMessage(log, m.sender, m.body);
    }
  });

  async function ensureConversation(): Promise<string> {
    if (conversationId) {
      if (connection.state === HubConnectionState.Connected) {
        await connection.invoke('JoinConversation', conversationId).catch(() => undefined);
      }
      return conversationId;
    }
    const result = await connection.invoke<{ id: string }>('StartConversation', options.visitorName ?? null);
    conversationId = result.id;
    localStorage.setItem(STORAGE_KEY(options.tenant), conversationId);
    return conversationId;
  }

  async function send() {
    const body = input.value.trim();
    if (!body || connection.state !== HubConnectionState.Connected) return;
    const id = await ensureConversation();
    await connection.invoke('SendMessage', id, body);
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

  connection
    .start()
    .then(() => {
      if (conversationId) void ensureConversation();
    })
    .catch(() => undefined);

  return {
    destroy: () => {
      void connection.stop();
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
