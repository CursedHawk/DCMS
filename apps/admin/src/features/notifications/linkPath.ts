/**
 * Turns a notification's stored `linkPath` into something the router can actually navigate to.
 *
 * Two jobs. The first is mechanical: a stored link is one string ("/content?instance=…"), and
 * TanStack Router wants the path and the search params as separate fields — handing it the
 * whole string as `to` makes it look for a route literally named "/content?instance=…", which
 * no route is, so it renders Not Found.
 *
 * The second is history. Content notifications used to be written as `/content/{guid}`, and the
 * SPA has never had a route of that shape — a content item is edited in a modal over
 * `/content`, not on a page of its own — so every one of those links landed on Not Found. The
 * producer is fixed now, but rows already in the notifications table keep the old form for as
 * long as the retention window lasts, and the bell reads them. So the old shape is rewritten
 * here rather than only at the source: fixing the writer alone would leave every notification
 * raised before the deploy exactly as broken as it was.
 *
 * Anything unrecognised passes through as a plain path. That is the right default — most links
 * are already bare routes ("/media", "/forms", "/sites/{id}") and inventing a rule for them is
 * the thing that would break them.
 */
export interface ResolvedLink {
  to: string;
  search: Record<string, string>;
}

export function resolveLinkPath(linkPath: string): ResolvedLink {
  const [rawPath, rawQuery] = linkPath.split('?', 2);
  const legacyInstance = legacyContentInstance(rawPath);
  const path = legacyInstance ? '/content' : rawPath;

  const search: Record<string, string> = {};
  for (const [key, value] of new URLSearchParams(rawQuery ?? '')) search[key] = value;
  if (legacyInstance) search.instance = legacyInstance;

  return { to: path, search };
}

const LEGACY_CONTENT =
  /^\/content\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;

/** The plugin-instance id out of a pre-fix `/content/{guid}` link, or null. */
function legacyContentInstance(path: string): string | null {
  return LEGACY_CONTENT.exec(path)?.[1] ?? null;
}

/**
 * The same thing, shaped for `navigate()`.
 *
 * The casts are not laziness. This app builds its routes through a `child()` helper, so the
 * router's generated union of valid `to` values only ever contains the three routes declared
 * literally ("/", "/auth/callback", "/sites/$siteId") — every navigate() call in the codebase
 * already casts past it. Doing it in one place keeps the cast next to the reason for it.
 */
export function linkTarget(linkPath: string) {
  const { to, search } = resolveLinkPath(linkPath);
  return { to: to as string, search: search as never };
}
