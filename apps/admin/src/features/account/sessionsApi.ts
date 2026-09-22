import { csrfHeader } from '@dcms/core';

// The edge's own endpoints, not a proxied API: a BFF session lives at the edge (ADR 0014), so
// the edge is the only thing that can list or end one. Same origin, so the session cookie rides
// along on its own and there is no bearer to attach.

export interface EdgeSessionSummary {
  /** Opaque. Only good for naming a row to end, and only alongside the session cookie. */
  id: string;
  /** The session this browser is signed in on. */
  current: boolean;
  createdAt: string;
  lastSeenAt: string;
  ip: string | null;
  /** Verbatim, from whatever the browser sent. Rendered as text — never as markup. */
  userAgent: string | null;
}

export const sessionsApi = {
  async list(): Promise<EdgeSessionSummary[]> {
    const res = await fetch('/.edge/sessions', { headers: { Accept: 'application/json' } });
    if (!res.ok) throw new Error(`GET /.edge/sessions → ${res.status}`);
    return (await res.json()) as EdgeSessionSummary[];
  },

  async revoke(id: string): Promise<void> {
    const res = await fetch(`/.edge/sessions/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      headers: csrfHeader(),
    });
    // 403 is the edge refusing a write whose CSRF token did not verify — a page that has been
    // open since before the token was reissued. Reloading gets a fresh one.
    if (!res.ok) throw new Error(`DELETE /.edge/sessions → ${res.status}`);
  },
};
