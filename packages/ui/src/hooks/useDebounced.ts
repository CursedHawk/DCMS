import { useEffect, useState } from 'react';

/**
 * A value that lags behind its input, so what follows it runs on a pause rather than a
 * keystroke.
 *
 * <p>Written for the search box above a server-filtered list. Without it every character
 * typed is a request, the answers race, and the list flickers through the results of prefixes
 * nobody asked about — including, occasionally, settling on one of them.</p>
 *
 * <p><b>The first value is not delayed.</b> A list whose filters were restored from the URL
 * should render its results immediately rather than showing everything for a beat first.</p>
 */
export function useDebounced<T>(value: T, delayMs = 300): T {
  const [settled, setSettled] = useState(value);

  useEffect(() => {
    if (Object.is(value, settled)) return;

    const id = setTimeout(() => setSettled(value), delayMs);
    return () => clearTimeout(id);
    // `settled` is deliberately absent: including it restarts the timer when the value it is
    // waiting for arrives, which is the one moment there is nothing left to wait for.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value, delayMs]);

  return settled;
}
