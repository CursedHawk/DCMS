import { useEffect, useState } from 'react';
import type { User } from 'oidc-client-ts';
import { renewSilently, userManager } from './auth';

export function useAuth(): { user: User | null; loading: boolean } {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let alive = true;

    // The stored user is only what this browser last heard. A sign-out elsewhere (the admin
    // console, another device) ends the login at identity, and the access token alone would
    // hide that for up to its 10-minute life. A refresh asks identity, which refuses once the
    // login is ended; renewSilently then drops the user and the shell shows the sign-in screen.
    // Only with a user to renew: signed out, signinSilent would try an iframe flow this client
    // has no silent redirect URI for.
    const check = async () =>
      (await userManager.getUser()) && (await renewSilently()) ? userManager.getUser() : null;

    void check().then((u) => {
      if (!alive) return;
      setUser(u);
      setLoading(false);
    });

    const loaded = (u: User) => setUser(u);
    const unloaded = () => setUser(null);
    userManager.events.addUserLoaded(loaded);
    userManager.events.addUserUnloaded(unloaded);
    userManager.events.addSilentRenewError(unloaded);

    // And again whenever the tab comes back, which is when a sign-out elsewhere matters.
    const recheck = () => {
      if (document.visibilityState === 'visible') void check();
    };
    document.addEventListener('visibilitychange', recheck);
    return () => {
      alive = false;
      document.removeEventListener('visibilitychange', recheck);
      userManager.events.removeUserLoaded(loaded);
      userManager.events.removeUserUnloaded(unloaded);
      userManager.events.removeSilentRenewError(unloaded);
    };
  }, []);

  return { user, loading };
}
