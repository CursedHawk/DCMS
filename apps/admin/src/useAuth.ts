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
    return () => {
      active = false;
      unsubscribe();
    };
  }, []);

  return { user, loading };
}
