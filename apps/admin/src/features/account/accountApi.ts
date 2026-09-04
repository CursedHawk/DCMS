import { getAccessToken } from '../../auth';
import { runtimeConfig } from '../../runtime-config';

// The account API is served by the identity service under /account/api/*. Identity has
// its own host (AUTH_HOST), so this is cross-origin in every deployment and identity's
// Cors:AllowedOrigins must list the console's origin. Falls back to the current origin
// only for a bare dev run with no authority configured. Auth is the bearer access token
// — no tenant header (account settings are tenant-agnostic).
const identityBase = runtimeConfig.oidcAuthority || window.location.origin;

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
  /**
   * Destroys the login and the mirrored git account. Identity refuses while the
   * user still belongs to any workspace, so this can never run before admin-api's
   * reversible half (`DELETE /api/admin/me`) has detached them.
   */
  deleteAccount: () => req<void>('/me', { method: 'DELETE' }),
};
