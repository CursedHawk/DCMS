import { useEffect, useState } from 'react';
import type { User } from 'oidc-client-ts';
import { userManager } from './auth';

export function useAuth(): { user: User | null; loading: boolean } {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let alive = true;
    void userManager.getUser().then((u) => {
      if (!alive) return;
      setUser(u);
      setLoading(false);
    });

    const loaded = (u: User) => setUser(u);
    const unloaded = () => setUser(null);
    userManager.events.addUserLoaded(loaded);
    userManager.events.addUserUnloaded(unloaded);
    userManager.events.addSilentRenewError(unloaded);
    return () => {
      alive = false;
      userManager.events.removeUserLoaded(loaded);
      userManager.events.removeUserUnloaded(unloaded);
      userManager.events.removeSilentRenewError(unloaded);
    };
  }, []);

  return { user, loading };
}
