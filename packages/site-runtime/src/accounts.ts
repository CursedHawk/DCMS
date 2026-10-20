import { useEffect, useSyncExternalStore } from 'react';
import { currentSiteEdge, onSiteAccessReset, resetSiteAccess } from './access';
import type { DataClient } from './data';

/**
 * Who is signed in on the site, for the account components (sign-in, signed-in sections, the
 * signed-in user). Two plugins sign people in, and they work nothing alike:
 *
 * - **User Authentication** (`users`): the workspace's own people, invited or admitted by their
 *   organisation. Signing in happens at the edge (`/.edge/site/signin`), on the provider's page —
 *   never in a form of the site's — and the session is a cookie the page never sees. The edge's
 *   `/me` says who it is.
 * - **Visitor Authentication** (`visitors`): self-registered accounts for the public. The site
 *   posts email and password to the instance (`/api/{instance}/login`, `/register`) and keeps the
 *   tokens it answers with in this browser; they go with every API call as a bearer, so other
 *   plugins (Forms, Dynamic Apps' "own records") know the visitor.
 *
 * Both are the experience, not the protection: pages are protected by User Authentication's
 * access rules at the edge, and data by each API.
 */
export type AccountKind = 'users' | 'visitors';

export interface SiteAccount {
  name: string | null;
  email: string | null;
}

/** `undefined`: not known yet; `null`: signed out. */
export type AccountState = SiteAccount | null | undefined;

const state: Record<AccountKind, AccountState> = { users: undefined, visitors: undefined };
const loading: Partial<Record<AccountKind, Promise<void>>> = {};
const listeners = new Set<() => void>();

function set(kind: AccountKind, value: AccountState): void {
  state[kind] = value;
  delete loading[kind];
  for (const listener of listeners) listener();
}

// Signed in, out or as someone else (a preview's popup, View as): ask again.
onSiteAccessReset(() => {
  set('users', undefined);
  set('visitors', undefined);
});

// --- Visitor Authentication's tokens --------------------------------------

const KEY = 'dcms.visitor';

interface VisitorSession {
  instance: string;
  accessToken: string;
  refreshToken: string;
}

function readSession(): VisitorSession | null {
  try {
    const value = JSON.parse(localStorage.getItem(KEY) ?? 'null') as Partial<VisitorSession> | null;
    return value && typeof value.instance === 'string' && typeof value.accessToken === 'string' && typeof value.refreshToken === 'string'
      ? (value as VisitorSession)
      : null;
  } catch {
    return null; // no storage (the prerender, private mode, blocked site data): signed out
  }
}

function writeSession(session: VisitorSession | null): void {
  try {
    if (session) localStorage.setItem(KEY, JSON.stringify(session));
    else localStorage.removeItem(KEY);
  } catch {
    /* storage refused: signed in for this page only, which the state below still is */
  }
}

/** The tokens a sign-in or a refresh answers with, or null for anything else. */
function tokensOf(json: unknown): { accessToken: string; refreshToken: string } | null {
  const t = json as { accessToken?: unknown; refreshToken?: unknown } | null;
  return t && typeof t.accessToken === 'string' && typeof t.refreshToken === 'string' ? { accessToken: t.accessToken, refreshToken: t.refreshToken } : null;
}

/** Whether a JWT is past (or within half a minute of) its `exp`. Unreadable: not expired; the API decides. */
function expired(token: string): boolean {
  try {
    const payload = JSON.parse(atob(token.split('.')[1]!.replace(/-/g, '+').replace(/_/g, '/'))) as { exp?: unknown };
    return typeof payload.exp === 'number' && payload.exp * 1000 < Date.now() + 30_000;
  } catch {
    return false;
  }
}

let refreshing: Promise<string | null> | null = null;

/**
 * The signed-in visitor's access token for an API call, renewed first when it has expired, or
 * null when nobody is. `refresh` posts the refresh token to `/api/{instance}/refresh` without a
 * bearer — each data client has its own way to reach the API. A refused renewal signs out.
 * ponytail: two tabs renewing at once rotate each other's refresh token out; the loser is
 * signed out. A cross-tab lock (navigator.locks) if that bites.
 */
export function visitorToken(refresh: (instance: string, refreshToken: string) => Promise<unknown>): Promise<string | null> {
  const session = readSession();
  if (!session) return Promise.resolve(null);
  if (!expired(session.accessToken)) return Promise.resolve(session.accessToken);
  refreshing ??= refresh(session.instance, session.refreshToken)
    .then(
      (json) => {
        const tokens = tokensOf(json);
        if (!tokens) throw new Error('no tokens');
        writeSession({ instance: session.instance, ...tokens });
        return tokens.accessToken;
      },
      () => null,
    )
    .catch(() => null)
    .then((token) => {
      if (!token) signOutVisitor();
      return token;
    })
    .finally(() => {
      refreshing = null;
    });
  return refreshing;
}

/** Signs a visitor in to `instance` (a Visitor Authentication instance) or makes their account: the tokens are kept. */
export async function signInVisitor(
  client: DataClient,
  instance: string,
  mode: 'sign-in' | 'register',
  body: { email: string; password: string; displayName?: string },
): Promise<void> {
  const tokens = tokensOf(await client.post(`/api/${instance}/${mode === 'register' ? 'register' : 'login'}`, body));
  if (!tokens) throw new Error('The sign-in answered without tokens.');
  writeSession({ instance, ...tokens });
  // Someone else now: forget what was fetched and asked as the last person; the components ask /me.
  resetSiteAccess();
}

/** Forgets the visitor's tokens. Nothing to tell the server: an access token lapses within minutes. */
export function signOutVisitor(): void {
  writeSession(null);
  resetSiteAccess();
  set('visitors', null);
}

// --- Who is signed in ------------------------------------------------------

async function load(kind: AccountKind, client: DataClient): Promise<void> {
  if (kind === 'users') {
    // The edge's own answer, with the session cookie. Not behind an edge (local development, a
    // site without sign-in): nobody is signed in.
    const res = await fetch(`${currentSiteEdge().base}/me`, { credentials: 'same-origin', headers: { Accept: 'application/json' } }).catch(() => null);
    const me = res?.ok ? ((await res.json().catch(() => null)) as { signedIn?: boolean; name?: string | null; email?: string | null } | null) : null;
    set('users', me?.signedIn ? { name: me.name ?? null, email: me.email ?? null } : null);
    return;
  }
  const session = readSession();
  if (!session) return set('visitors', null);
  try {
    // The client sends the token (and renews it); a refused one is a visitor no longer signed in.
    const me = (await client.get(`/api/${session.instance}/me`)) as { email?: string | null; displayName?: string | null };
    set('visitors', { name: me.displayName || me.email || null, email: me.email ?? null });
  } catch (e) {
    if ((e as { status?: number }).status === 401) signOutVisitor();
    else set('visitors', null);
  }
}

/**
 * Who is signed in with `kind`, asked once per page load (and again after a sign-in, a sign-out
 * or a preview's View as). `undefined` until known, always on the server and on the canvas
 * (`enabled` false), so a prerendered page and its first client render agree.
 */
export function useAccount(kind: AccountKind, client: DataClient, enabled = true): AccountState {
  const value = useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => (enabled ? state[kind] : undefined),
    () => undefined,
  );
  useEffect(() => {
    if (!enabled || value !== undefined || loading[kind]) return;
    loading[kind] = load(kind, client);
  }, [enabled, value, kind, client]);
  return value;
}

/** Sends the visitor to the edge's sign-in and back to `returnUrl` (a preview opens its popup). */
export function signInUser(returnUrl: string): void {
  currentSiteEdge().signIn(returnUrl);
}

/** Signs the user out of the site at the edge (a preview opens its popup). */
export function signOutUser(): void {
  currentSiteEdge().signOut();
}
