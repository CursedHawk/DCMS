import userEvent from '@testing-library/user-event';

/**
 * `userEvent`, configured for Radix under jsdom.
 *
 * `pointerEventsCheck: 0` because an open Radix overlay marks everything behind it
 * `pointer-events: none` — correct in a browser, and correct here — which then makes userEvent
 * refuse to click the trigger that would close it, on the grounds that a real mouse could not
 * have. jsdom does no layout, so the check is guesswork either way.
 *
 * ---
 *
 * **A note on what is deliberately not unit-tested here.**
 *
 * Radix's floating overlays — popover, dropdown, select, tooltip — are ruinously slow under
 * jsdom. Measured: a single test that opens one popover takes 3–20 seconds, and it degrades as
 * more accumulate within a file. The cause is Floating UI re-measuring against a DOM that
 * performs no layout and therefore reports every element as a zero-sized box; it never settles
 * and never errors, it just keeps trying.
 *
 * Since jsdom has no layout, such a test also cannot check the thing a popover is actually at
 * risk of getting wrong — where it lands, whether it is clipped, whether it is reachable. So
 * the trade is bad in both directions.
 *
 * Everything observable without opening an overlay is unit-tested: the trigger's accessible
 * name, badge counts, disabled states, and the row/content components rendered directly.
 * "Open the thing and click inside it" belongs in Playwright, where a real browser makes it
 * both fast and honest.
 */
export const user = userEvent.setup({ delay: null, pointerEventsCheck: 0 });
