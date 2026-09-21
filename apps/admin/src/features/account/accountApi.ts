import { csrfHeader } from '@dcms/core';
import { getAccessToken, isBffMode } from '../../auth';
import { runtimeConfig } from '../../runtime-config';

// The account API is served by the identity service under /account/api/*.
//
// In BEARER mode that is identity's own host (AUTH_HOST), so the call is cross-origin in every
// deployment and identity's Cors:AllowedOrigins has to list the console's origin.
//
// In BFF mode it is SAME-ORIGIN: the edge routes /account/api on the admin host to identity
// (ADR 0014), which is what lets it attach the session's bearer — there is no token here to
// send. That also retires the CORS dependency, which is a second reason to prefer it once
// bearer mode is gone.
//
// No tenant header either way: account settings are tenant-agnostic.
const identityBase = isBffMode ? '' : runtimeConfig.oidcAuthority || window.location.origin;

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
      // Empty in bearer mode, where there is no such cookie and nothing checks for one.
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
