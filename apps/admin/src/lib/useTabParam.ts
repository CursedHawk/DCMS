import { useNavigate, useSearch } from '@tanstack/react-router';
import { useCallback } from 'react';

/**
 * A page's tab, kept in the URL as `?<name>=`.
 *
 * <p>Tabs that change what the page shows belong in the address: a reload, a shared link and
 * the back button all return to the same view. Only the plugin instance page did that; Content,
 * Jobs and a plugin's reference held theirs in component state and forgot them on every reload.
 * The default value is left out of the URL so a page's plain address stays plain.</p>
 *
 * <p>`replace`, not push: moving between tabs is looking around one page, and a history entry
 * per click would make Back walk through every tab before leaving it.</p>
 */
export function useTabParam<T extends string>(
  name: string,
  values: readonly T[],
  fallback: T,
): [T, (next: string) => void] {
  const search = useSearch({ strict: false }) as Record<string, unknown>;
  const navigate = useNavigate();
  const raw = search[name];
  const value = typeof raw === 'string' && (values as readonly string[]).includes(raw) ? (raw as T) : fallback;

  const set = useCallback(
    (next: string) =>
      void navigate({
        to: '.',
        search: ((prev: Record<string, unknown>) => ({ ...prev, [name]: next === fallback ? undefined : next })) as never,
        replace: true,
      }),
    [navigate, name, fallback],
  );

  return [value, set];
}
