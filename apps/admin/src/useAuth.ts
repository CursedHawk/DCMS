import { useEffect, useState } from 'react';
import type { AuthSession } from '@dcms/core';
import { getUser, subscribeToAuth } from './auth';

export interface AuthState {
  user: AuthSession | null;
  loading: boolean;
}

/**
 * Tracks the signed-in operator, in either authentication mode.
 *
 * <p>The shell reads `user?.profile.sub` (which identifies this browser to the notification and
 * site hubs) and `name`/`email` for the avatar menu — and nothing else, which is why one shape
 * serves both modes. In bearer mode the subscription is the UserManager's load/unload/renew-error
 * events; in BFF mode it is whatever `/.edge/me` last said. Neither is visible from here, which
 * is the point of putting it behind AuthClient.</p>
 */
export function useAuth(): AuthState {
  const [user, setUser] = useState<AuthSession | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let active = true;
    void getUser().then((u) => {
      if (active) {
        setUser(u);
        setLoading(false);
      }
    });

    const unsubscribe = subscribeToAuth((u) => setUser(u));

    // Re-asked when the tab comes back, so a sign-out in the platform console or on another
    // device lands here as the sign-in screen rather than as a shell whose calls all 401.
    // getUser publishes the change through the subscription above.
    const recheck = () => {
      if (document.visibilityState === 'visible') void getUser();
    };
    document.addEventListener('visibilitychange', recheck);
    return () => {
      active = false;
      document.removeEventListener('visibilitychange', recheck);
      unsubscribe();
    };
  }, []);

  return { user, loading };
}
