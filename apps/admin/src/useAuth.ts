import { useEffect, useState } from 'react';
import type { User } from 'oidc-client-ts';
import { getUser, userManager } from './auth';

export interface AuthState {
  user: User | null;
  loading: boolean;
}

/**
 * Tracks the current OIDC user and keeps it in sync with token renewals and
 * sign-outs. Components read user?.profile and user?.access_token from here.
 */
export function useAuth(): AuthState {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let active = true;
    getUser().then((u) => {
      if (active) {
        setUser(u);
        setLoading(false);
      }
    });

    const onLoaded = (u: User) => setUser(u);
    const onUnloaded = () => setUser(null);
    // A background renewal that fails leaves an unusable token in the store;
    // drop it so the shell shows the sign-in screen instead of 401-ing forever.
    const onRenewError = (err: unknown) => {
      console.warn('OIDC silent renew failed; signing out locally.', err);
      void userManager.removeUser();
    };
    userManager.events.addUserLoaded(onLoaded);
    userManager.events.addUserUnloaded(onUnloaded);
    userManager.events.addSilentRenewError(onRenewError);

    return () => {
      active = false;
      userManager.events.removeUserLoaded(onLoaded);
      userManager.events.removeUserUnloaded(onUnloaded);
      userManager.events.removeSilentRenewError(onRenewError);
    };
  }, []);

  return { user, loading };
}
