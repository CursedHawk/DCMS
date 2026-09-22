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

/** Carries the status so a caller can tell "still signed in" from "reload and retry". */
export class SessionRevokeError extends Error {
  constructor(readonly status: number) {
    super(`DELETE /.edge/sessions \u2192 ${status}`);
    this.name = 'SessionRevokeError';
  }
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
    // Every failure means the same thing to the person reading it: that device is still signed
    // in. 502 is the edge unable to reach identity to end the login behind the session, and it
    // deliberately leaves the session in place rather than reporting a sign-out that did not
    // happen; 403 is a CSRF token older than the one the edge now expects, which a reload fixes.
    if (!res.ok) throw new SessionRevokeError(res.status);
  },
};
