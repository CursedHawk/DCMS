import { createContext, useContext } from 'react';

/**
 * Whether a component is being edited or used.
 *
 * - `edit` — the builder canvas. A click selects; actions do not run; links do not navigate.
 *   An accordion stays laid out so its parts can be picked, not hidden behind a toggle.
 * - `live` — the published site, and the builder's preview. Everything behaves for real.
 *
 * One component implementation serves both, reading this, rather than an editor double that
 * drifts from what ships. Preview is `live` on purpose: anything it did differently from the
 * published site would be a preview that lies.
 */
export type RenderMode = 'edit' | 'live';

export const RenderModeContext = createContext<RenderMode>('live');

export function useRenderMode(): RenderMode {
  return useContext(RenderModeContext);
}
