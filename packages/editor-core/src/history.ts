/** Snapshot-based undo/redo history for an immutable state value (e.g. a SiteDefinition). */
export interface History<T> {
  present: T;
  past: T[];
  future: T[];
}

export function initHistory<T>(present: T): History<T> {
  return { present, past: [], future: [] };
}

/** Push a new present, clearing the redo stack. Optionally cap the undo depth. */
export function push<T>(history: History<T>, next: T, limit = 100): History<T> {
  const past = [...history.past, history.present];
  if (past.length > limit) past.shift();
  return { present: next, past, future: [] };
}

export function canUndo<T>(history: History<T>): boolean {
  return history.past.length > 0;
}

export function canRedo<T>(history: History<T>): boolean {
  return history.future.length > 0;
}

export function undo<T>(history: History<T>): History<T> {
  if (history.past.length === 0) return history;
  const previous = history.past[history.past.length - 1];
  return {
    present: previous,
    past: history.past.slice(0, -1),
    future: [history.present, ...history.future],
  };
}

export function redo<T>(history: History<T>): History<T> {
  if (history.future.length === 0) return history;
  const next = history.future[0];
  return {
    present: next,
    past: [...history.past, history.present],
    future: history.future.slice(1),
  };
}
