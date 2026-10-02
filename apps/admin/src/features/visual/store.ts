import { APP_JSON, appSchema, builtinRegistry, type App, type Registry } from '@dcms/site-runtime';
import { create } from 'zustand';
import type { DeviceId } from './canvas/editor';

/** What the canvas is editing: one page, or the app shell every page renders inside. */
export type CanvasTarget =
  | { kind: 'page'; id: string }
  | { kind: 'shell' }
  /** A component's template, in the composer (P3). */
  | { kind: 'component'; name: string; version: number };

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
  /** The built-ins plus this site's own components; what the canvas, inspector and validator use. */
  registry: Registry;
  /**
   * Component types that may not be dropped right now: while a component is open in the
   * composer, itself and every component that already contains it (a loop would render forever).
   */
  blockedTypes: ReadonlySet<string>;
  /** Write the canvas's pending edits to its file now (set by the canvas while it is mounted). */
  flushCanvas: () => void;
  setRegistry: (registry: Registry) => void;
  setBlockedTypes: (types: ReadonlySet<string>) => void;
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
  registry: builtinRegistry,
  blockedTypes: new Set(),
  flushCanvas: () => {},
  setRegistry: (registry) => set({ registry }),
  setBlockedTypes: (blockedTypes) => set({ blockedTypes }),
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
  if (a.kind !== b.kind) return false;
  if (a.kind === 'page') return a.id === (b as { id: string }).id;
  if (a.kind === 'component') {
    const c = b as { name: string; version: number };
    return a.name === c.name && a.version === c.version;
  }
  return true;
}

export { APP_JSON };
