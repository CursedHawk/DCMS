import { csrfHeader } from '@dcms/core';

// The account API is served by identity under /account/api/*, reached SAME-ORIGIN: the edge
// routes that prefix on the admin host to identity (ADR 0014), which is what lets it attach the
// session's bearer — there is no token here to send.
//
// It used to be called on identity's own host, cross-origin, which meant identity's
// Cors:AllowedOrigins had to list the console. Nothing depends on that entry any more.
//
// No tenant header: account settings are tenant-agnostic.

export interface AccountMe {
  email: string | null;
  displayName: string | null;
  forgejoUsername: string | null;
  hasGitPassword: boolean;
}

export interface SshKey {
  id: number;
  title: string;
  fingerprint: string | null;
  createdAt: string;
}

async function req<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`/account/api${path}`, {
    ...init,
    headers: {
      ...csrfHeader(),
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...init?.headers,
    },
  });
  if (!res.ok) {
    let message = `${init?.method ?? 'GET'} ${path} → ${res.status}`;
    try {
      const body = (await res.json()) as { error?: string };
      if (body?.error) message = body.error;
    } catch {
      /* non-JSON */
    }
    throw new Error(message);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export const accountApi = {
  me: () => req<AccountMe>('/me'),
  setPassword: (body: { currentPassword?: string; newPassword: string }) =>
    req<void>('/password', { method: 'POST', body: JSON.stringify(body) }),
  listKeys: () => req<SshKey[]>('/ssh-keys'),
  addKey: (body: { title: string; key: string }) =>
    req<SshKey>('/ssh-keys', { method: 'POST', body: JSON.stringify(body) }),
  deleteKey: (id: number) => req<void>(`/ssh-keys/${id}`, { method: 'DELETE' }),
  /**
   * Destroys the login and the mirrored git account. Identity refuses while the
   * user still belongs to any workspace, so this can never run before admin-api's
   * reversible half (`DELETE /api/admin/me`) has detached them.
   */
  deleteAccount: () => req<void>('/me', { method: 'DELETE' }),
};
