import { useEffect, useState } from 'react';
import { ApiError, createTenantClient, type SiteAccess, type SiteUser } from '../api/runtime';

/**
 * Who may open what, for a site behind User Authentication.
 *
 * The site's access rules are enforced by the edge on every request that reaches it — but a
 * single-page app moves between its pages without a request, so those rules never see a click
 * inside the app. These helpers ask the edge before a route is drawn and, when the answer is
 * "not here", leave the app so the server answers itself: its sign-in, or its "no access" page.
 *
 * This is the experience, not the protection. Whatever a route shows from the API is protected
 * by the API's own access (Access → API access in the console); a route's code ships to anyone
 * who can load the app.
 */

// The edge is this site's own origin: its rules and sessions answer same-origin only.
const edge = createTenantClient({ baseUrl: '' });
const known = new Map<string, Promise<SiteAccess>>();

if (typeof window !== 'undefined') {
  // This document was served, so the edge already let this visitor have this path.
  known.set(window.location.pathname, Promise.resolve('allow'));
}

/** The site's rules on `path` for this visitor, asked once per path per page load. */
export function siteAccess(path: string): Promise<SiteAccess> {
  let answer = known.get(path);
  if (!answer) {
    answer = edge.siteAccess([path]).then((r) => r[path] ?? 'allow');
    known.set(path, answer);
    // A failed question is not an answer: ask again next time.
    answer.catch(() => known.delete(path));
  }
  return answer;
}

/**
 * Resolves true when `path` may be drawn. Otherwise sends the browser to the edge — its sign-in,
 * coming back here, or the path itself as a real page load, which the edge answers with its own
 * "no access" page — and never resolves. If the edge cannot be asked, the route is drawn: the
 * server still guards its data.
 */
export async function guardSiteRoute(path: string): Promise<boolean> {
  const access = await siteAccess(path).catch(() => 'allow' as const);
  if (access === 'allow') return true;
  window.location.assign(access === 'signin' ? edge.siteSignInUrl(path) : path);
  return new Promise<never>(() => {});
}

/**
 * A react-router loader guarding every navigation. Put it on the root route together with
 * `shouldRevalidate: siteAccessRevalidate`, so it runs before each new page is drawn:
 *
 *   createBrowserRouter([{ element: <Layout />, loader: siteAccessLoader, shouldRevalidate: siteAccessRevalidate, children }])
 */
export async function siteAccessLoader({ request }: { request: Request }): Promise<null> {
  await guardSiteRoute(new URL(request.url).pathname);
  return null;
}

/** Re-runs the root loader whenever the path changes, which react-router does not by itself. */
export function siteAccessRevalidate({
  currentUrl,
  nextUrl,
  defaultShouldRevalidate,
}: {
  currentUrl: URL;
  nextUrl: URL;
  defaultShouldRevalidate: boolean;
}): boolean {
  return currentUrl.pathname !== nextUrl.pathname || defaultShouldRevalidate;
}

/** The signed-in user (null when nobody is), or undefined while it is being asked. */
export function useSiteUser(): SiteUser | null | undefined {
  const [user, setUser] = useState<SiteUser | null | undefined>(undefined);
  useEffect(() => {
    let live = true;
    void edge.siteUser().then((u) => live && setUser(u));
    return () => {
      live = false;
    };
  }, []);
  return user;
}

/** The site's rules on `path` for this visitor — to show or hide a link — or undefined while asked. */
export function useSiteAccess(path: string): SiteAccess | undefined {
  const [access, setAccess] = useState<SiteAccess | undefined>(undefined);
  useEffect(() => {
    let live = true;
    setAccess(undefined);
    siteAccess(path)
      .then((a) => live && setAccess(a))
      .catch(() => live && setAccess('allow'));
    return () => {
      live = false;
    };
  }, [path]);
  return access;
}

/**
 * For an API call's error: when the session behind a long-open app has ended (a 401 "sign in
 * first" from the site), sends the visitor to sign in and back, and returns true.
 */
export function signInIfRequired(error: unknown): boolean {
  if (!(error instanceof ApiError) || !error.signInRequired) return false;
  window.location.assign(edge.siteSignInUrl());
  return true;
}
