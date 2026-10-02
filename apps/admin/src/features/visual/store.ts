import { APP_JSON, appSchema, type App } from '@dcms/site-runtime';
import { create } from 'zustand';
import type { DeviceId } from './canvas/editor';

/** What the canvas is editing: one page, or the app shell every page renders inside. */
export type CanvasTarget = { kind: 'page'; id: string } | { kind: 'shell' };

export type VisualView = 'design' | 'split' | 'code' | 'preview';

interface VisualState {
  target: CanvasTarget | null;
  device: DeviceId;
  view: VisualView;
  /**
   * The app document as last parsed, for the components that read it on the canvas (menus).
   * Module state rather than React context because every node on the canvas has its own React
   * root, and a context does not cross roots — a store does.
   */
  app: App | null;
  setTarget: (target: CanvasTarget) => void;
  setDevice: (device: DeviceId) => void;
  setView: (view: VisualView) => void;
  syncApp: (text: string | undefined) => void;
}

export const useVisual = create<VisualState>((set, get) => ({
  target: null,
  device: 'desktop',
  view: 'design',
  app: null,
  setTarget: (target) => set({ target }),
  setDevice: (device) => set({ device }),
  setView: (view) => set({ view }),
  syncApp: (text) => {
    const app = parseApp(text);
    // Keep the old object when the content is the same, so the canvas does not re-render on
    // every keystroke elsewhere in the file map.
    if (JSON.stringify(app) !== JSON.stringify(get().app)) set({ app });
  },
}));

export function parseApp(text: string | undefined): App | null {
  if (!text) return null;
  try {
    const parsed = appSchema.safeParse(JSON.parse(text));
    return parsed.success ? parsed.data : null;
  } catch {
    return null;
  }
}

export function sameTarget(a: CanvasTarget | null, b: CanvasTarget | null): boolean {
  if (!a || !b) return a === b;
  return a.kind === b.kind && (a.kind === 'shell' || a.id === (b as { id: string }).id);
}

export { APP_JSON };
