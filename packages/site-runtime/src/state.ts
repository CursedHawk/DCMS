import { createContext, useContext, useSyncExternalStore } from 'react';
import { z } from 'zod';
import { NAME } from './ids';

/**
 * Page state (backlog #123): a page declares a few named values — which tab is open, whether a
 * panel is expanded — with defaults; `set-state` / `toggle-state` actions change them; a node's
 * `when` shows it only while a value matches. That is the whole model: no expressions, no
 * derived values, nothing an author cannot read in the inspector.
 *
 * Values live in one small store shared by every React root (the canvas gives each node its
 * own), keyed by page, with the page's declared defaults underneath until something is set.
 */

export const stateValueSchema = z.union([z.boolean(), z.number(), z.string().max(200)]);
export type StateValue = z.infer<typeof stateValueSchema>;

/** `page.state`: name → default value. */
export const pageStateSchema = z.record(z.string().regex(NAME), stateValueSchema);
export type PageState = z.infer<typeof pageStateSchema>;

/** `node.when`: shown while `state` equals `equals` — or, without `equals`, while it is truthy. */
export const whenSchema = z.strictObject({ state: z.string().regex(NAME), equals: stateValueSchema.optional() });
export type When = z.infer<typeof whenSchema>;

export interface PageStateScope {
  pageId: string;
  defaults: PageState;
}

/** The page whose state a node reads and its actions write. Null outside a page (the shell). */
export const PageStateContext = createContext<PageStateScope | null>(null);

const values = new Map<string, Record<string, StateValue>>();
const listeners = new Set<() => void>();

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function readState(scope: PageStateScope, key: string): StateValue | undefined {
  return values.get(scope.pageId)?.[key] ?? scope.defaults[key];
}

export function writeState(scope: PageStateScope, key: string, value: StateValue | ((current: StateValue | undefined) => StateValue)): void {
  if (!(key in scope.defaults)) return;
  const next = typeof value === 'function' ? value(readState(scope, key)) : value;
  values.set(scope.pageId, { ...values.get(scope.pageId), [key]: next });
  listeners.forEach((l) => l());
}

/** Back to every page's defaults (tests; a site never needs it). */
export function resetPageState(): void {
  values.clear();
  listeners.forEach((l) => l());
}

export function matches(when: When, value: StateValue | undefined): boolean {
  return when.equals === undefined ? !!value : value === when.equals;
}

/** Whether a node with this `when` is shown now. Always true without one, or outside a page. */
export function useShown(when: When | undefined): boolean {
  const scope = useContext(PageStateContext);
  return useSyncExternalStore(
    subscribe,
    () => !when || !scope || matches(when, readState(scope, when.state)),
    () => !when || !scope || matches(when, scope?.defaults[when.state]),
  );
}
