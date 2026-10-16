/**
 * A parameter of the address the screen was opened with: how the jobs page's links land on the
 * right tab, flow, run or table (`/plugins/crm/configuration?tab=automation&run=…`). Read once,
 * as the screen's starting state; moving around afterwards does not rewrite the address.
 */
export function deepLink(name: string): string | null {
  return typeof window === 'undefined' ? null : new URLSearchParams(window.location.search).get(name);
}
