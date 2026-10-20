import type { RouteObject, ShouldRevalidateFunctionArgs } from 'react-router';

/**
 * The site's access rules (User Authentication) for the app's own navigation.
 *
 * The edge enforces them on every request that reaches it, but moving between pages inside the
 * app is no request. So before a route is drawn the app asks the edge, and when the answer is
 * "not here" leaves for the server's own answer: its sign-in, or the path as a real page load,
 * which the edge answers with its "no access" page. The experience, not the protection: a
 * page's data is protected by its API's access, and the app's code ships to anyone who loads it.
 */
export type SiteAccess = 'allow' | 'signin' | 'forbidden' | 'unavailable';

const known = new Map<string, Promise<SiteAccess>>();

/** Where the site's edge answers, and what leaving the app for one of its answers means. */
export interface SiteEdge {
  /** `/.edge/site` on a published site; a builder's preview has its own (#269). */
  base: string;
  /** Leave the app for the edge's sign-in (`access` = `signin`) or its "no access" page load. */
  leave: (url: string, access: SiteAccess) => void;
  /** A sign-in button: the edge's sign-in, coming back to `returnUrl`. A preview opens its popup. */
  signIn: (returnUrl: string) => void;
  /** A sign-out button: the edge's sign-out (of the site and of the workspace's sign-in). */
  signOut: () => void;
}

/**
 * Leaves the app for the edge. A sandboxed builder preview (the developer components' build) cannot
 * navigate its frame to the edge, so it defines `__dcmsPreviewLeave` and opens its popup instead.
 */
function go(url: string): void {
  const preview = (window as { __dcmsPreviewLeave?: (url: string) => void }).__dcmsPreviewLeave;
  if (preview) preview(url);
  else window.location.assign(url);
}

let edge: SiteEdge = {
  base: '/.edge/site',
  leave: go,
  signIn: (returnUrl) => go(`${edge.base}/signin?returnUrl=${encodeURIComponent(returnUrl)}`),
  signOut: () => go(`${edge.base}/signout`),
};

const resets = new Set<() => void>();

/** Called whenever the answers are forgotten — whoever is signed in may have changed. */
export function onSiteAccessReset(listener: () => void): void {
  resets.add(listener);
}

/** The edge the runtime asks now. */
export function currentSiteEdge(): SiteEdge {
  return edge;
}

/**
 * Points the runtime at another edge: a builder's preview asks the console's preview endpoints,
 * and opens sign-in in a popup rather than navigating its frame away. Forgets every answer.
 */
export function configureSiteEdge(next: Partial<SiteEdge>): void {
  edge = { ...edge, ...next };
  resetSiteAccess();
}

/** Forgets every answer: the visitor has signed in or out. */
export function resetSiteAccess(): void {
  known.clear();
  for (const listener of resets) listener();
}

/** The site's rules on `path` for this visitor, asked once per path per page load. */
export function siteAccess(path: string): Promise<SiteAccess> {
  let answer = known.get(path);
  if (!answer) {
    answer = fetch(`${edge.base}/access?path=${encodeURIComponent(path)}`, {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    }).then(async (res) => {
      // Not behind the edge (a preview elsewhere): there are no rules to ask.
      if (res.status === 404) return 'allow';
      if (!res.ok) throw new Error(`site access check failed: ${res.status}`);
      return ((await res.json()) as Record<string, SiteAccess>)[path] ?? 'allow';
    });
    known.set(path, answer);
    // A failed question is not an answer: ask again next time.
    answer.catch(() => known.delete(path));
  }
  return answer;
}

/**
 * Resolves true when `path` may be drawn; otherwise leaves for the edge (see {@link SiteEdge}) and
 * never resolves. If the edge cannot be asked the route is drawn: the server still guards its data.
 */
export async function guardSiteRoute(path: string): Promise<boolean> {
  const access = await siteAccess(path).catch(() => 'allow' as const);
  if (access === 'allow') return true;
  edge.leave(access === 'signin' ? `${edge.base}/signin?returnUrl=${encodeURIComponent(path)}` : path, access);
  return new Promise<never>(() => {});
}

const SHELL = 'dcms-shell';

/**
 * The site's routes with the guard on the shell, for the published site's browser router, and
 * the hydration data that lets the first page — the edge already let this visitor have it — draw
 * without asking. Never for the builder's preview or the prerender, which have no edge.
 */
export function guardedRoutes(routes: RouteObject[]): { routes: RouteObject[]; hydrationData: { loaderData: Record<string, null> } } {
  const [shell, ...rest] = routes;
  return {
    routes: [
      {
        ...shell!,
        id: SHELL,
        loader: async ({ request }) => {
          await guardSiteRoute(new URL(request.url).pathname);
          return null;
        },
        shouldRevalidate: ({ currentUrl, nextUrl, defaultShouldRevalidate }: ShouldRevalidateFunctionArgs) =>
          currentUrl.pathname !== nextUrl.pathname || defaultShouldRevalidate,
      } as RouteObject,
      ...rest,
    ],
    hydrationData: { loaderData: { [SHELL]: null } },
  };
}
