import { getAccessToken } from '../../auth';

// The account API is served by the identity service under /account/api/*. In prod
// it is same-origin (Caddy routes /account → identity); in dev the OIDC authority
// origin is used (identity enables CORS for the SPA origin). Auth is the bearer
// access token — no tenant header (account settings are tenant-agnostic).
const identityBase = import.meta.env.VITE_OIDC_AUTHORITY ?? window.location.origin;

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
  const token = await getAccessToken();
  const res = await fetch(`${identityBase}/account/api${path}`, {
    ...init,
    headers: {
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
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
};
