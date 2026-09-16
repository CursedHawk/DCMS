import { useEffect, useState, type DependencyList } from 'react';

export type ApiState<T> =
  | { status: 'loading'; data?: undefined; error?: undefined }
  | { status: 'error'; data?: undefined; error: Error }
  | { status: 'success'; data: T; error?: undefined };

/**
 * Load something from the API when a component mounts, and again whenever `deps` change.
 *
 * ```tsx
 * const posts = useApi(() => api.content('news', 'post').list({ pageSize: 6 }), []);
 * if (posts.status === 'loading') return <Loading />;
 * if (posts.status === 'error') return <ErrorMessage error={posts.error} onRetry={posts.reload} />;
 * return <List items={posts.data.items} />;
 * ```
 *
 * A response that arrives after the component unmounted, or after `deps` changed, is dropped —
 * which is what stops a slow first request overwriting a faster second one.
 */
export function useApi<T>(load: () => Promise<T>, deps: DependencyList): ApiState<T> & { reload: () => void } {
  const [state, setState] = useState<ApiState<T>>({ status: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let current = true;
    setState({ status: 'loading' });
    load().then(
      (data) => {
        if (current) setState({ status: 'success', data });
      },
      (error: unknown) => {
        if (current) setState({ status: 'error', error: error instanceof Error ? error : new Error(String(error)) });
      },
    );
    return () => {
      current = false;
    };
    // `load` is a new function every render; `deps` says when it means something different.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, attempt]);

  return { ...state, reload: () => setAttempt((n) => n + 1) };
}
