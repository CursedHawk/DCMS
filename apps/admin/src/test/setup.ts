import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

afterEach(cleanup);

/*
 * The runtime configuration the entrypoint HTML injects.
 *
 * `runtime-config.ts` reads `window.__DCMS_CONFIG__` at module load, so importing anything
 * that transitively reaches an API client throws before a single test runs without this. The
 * placeholder form is what `vite dev` serves too — the entrypoint's `__DCMS_*__` tokens are
 * substituted by the container entrypoint, not by Vite — and runtime-config already knows to
 * treat an unsubstituted placeholder as absent.
 */
// The type is already declared by `@dcms/core`'s runtime-config; redeclaring it here with a
// narrower value type is a conflicting augmentation, not an addition.
window.__DCMS_CONFIG__ = {};

globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

Element.prototype.scrollIntoView ??= () => {};
Element.prototype.hasPointerCapture ??= () => false;

window.matchMedia ??= ((query: string) => ({
  matches: false,
  media: query,
  onchange: null,
  addEventListener: () => {},
  removeEventListener: () => {},
  addListener: () => {},
  removeListener: () => {},
  dispatchEvent: () => false,
})) as typeof window.matchMedia;

export {};
