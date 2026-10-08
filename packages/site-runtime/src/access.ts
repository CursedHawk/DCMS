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

/** The site's rules on `path` for this visitor, asked once per path per page load. */
export function siteAccess(path: string): Promise<SiteAccess> {
  let answer = known.get(path);
  if (!answer) {
    answer = fetch(`/.edge/site/access?path=${encodeURIComponent(path)}`, {
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
 * Resolves true when `path` may be drawn; otherwise sends the browser to the edge and never
 * resolves. If the edge cannot be asked the route is drawn: the server still guards its data.
 */
export async function guardSiteRoute(path: string): Promise<boolean> {
  const access = await siteAccess(path).catch(() => 'allow' as const);
  if (access === 'allow') return true;
  window.location.assign(access === 'signin' ? `/.edge/site/signin?returnUrl=${encodeURIComponent(path)}` : path);
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
