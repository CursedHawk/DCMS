import { useSyncExternalStore } from 'react';

/**
 * Whether each live connection is currently up, readable from anywhere in the tree.
 *
 * <p><b>Why a registry and not a prop.</b> The hub is opened once in the app shell, but the
 * components that depend on it being up are several levels down — the Deployments panel is
 * inside a sidebar inside a page. Threading a boolean through those layers makes every
 * intermediate component know about a socket it has no other business with, and there is no
 * React context here that both the shell and a deeply nested panel already share.</p>
 *
 * <p>A hub nobody has reported on reads as <c>undefined</c>, which is deliberately distinct
 * from <c>false</c>: "no socket was ever opened for this" and "the socket is down" call for
 * different behaviour, and collapsing them would make an app with no hub mounted look
 * permanently disconnected.</p>
 */

const connections = new Map<string, boolean>();
const listeners = new Set<() => void>();

/** Called by each hub hook as its connection opens, drops and reconnects. */
export function setHubConnected(hub: string, connected: boolean): void {
  if (connections.get(hub) === connected) return;
  connections.set(hub, connected);
  for (const listener of listeners) listener();
}

/** Non-reactive read, for code outside a component. */
export function hubConnected(hub: string): boolean | undefined {
  return connections.get(hub);
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** `undefined` until some hub hook has reported on this name. */
export function useHubConnected(hub: string): boolean | undefined {
  return useSyncExternalStore(
    subscribe,
    () => connections.get(hub),
    () => undefined,
  );
}

/** Test seam. Nothing in the app calls this. */
export function resetHubPresence(): void {
  connections.clear();
  listeners.clear();
}
