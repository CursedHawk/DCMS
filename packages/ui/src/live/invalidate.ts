import type { QueryClient } from '@tanstack/react-query';
import type { ResourceChange } from './tags';

/**
 * Which query keys a tag makes stale.
 *
 * Written by each app, because query keys belong to the app. A tag maps to one or more key
 * *prefixes*: react-query matches prefixes, so `['media']` covers `['media', folderId, search]`
 * and every other variation of the media list without the server needing to know any of them.
 */
export type TagQueryMap = Readonly<Record<string, readonly (readonly unknown[])[]>>;

/**
 * Turns a resource change into refetches.
 *
 * <p>Returns a plain function rather than a hook so the decision — which tag invalidates what —
 * is testable without rendering anything, and so the socket handler can call it through a ref
 * without re-subscribing on every render.</p>
 *
 * <p>An unknown tag is ignored rather than treated as "refetch everything". A console talking
 * to a newer server would otherwise refetch its entire cache on every push it did not
 * understand, which is the opposite of what the tag was for.</p>
 */
export function createResourceInvalidator(
  queryClient: QueryClient,
  map: TagQueryMap,
): (change: ResourceChange) => void {
  return (change) => {
    const keys = map[change.tag];
    if (!keys) return;
    for (const queryKey of keys) {
      void queryClient.invalidateQueries({ queryKey });
    }
  };
}
