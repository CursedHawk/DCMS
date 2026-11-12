import { QueryClient } from '@tanstack/react-query';

/**
 * The app's one query cache, as a module rather than only through React context.
 *
 * <p>The agent host runs site agents that outlive every component (ADR 0033), and it still has
 * to read and invalidate the same cache the pages use: a second client would mean a second copy
 * of every query and invalidations nobody's screen hears.</p>
 */
export const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false } },
});
