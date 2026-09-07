import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

afterEach(cleanup);

/*
 * Tells React that `act()` is legitimate here. React Testing Library sets this for the work it
 * drives itself, but a test that reaches for `act` directly — to advance a fake timer inside an
 * effect, say — gets a warning without it.
 */
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

/*
 * Radix measures and positions with APIs jsdom does not implement. Without these, every
 * component built on a Popper (select, dropdown, popover, tooltip) throws on open rather
 * than rendering, and the failure reads as a component bug rather than a missing polyfill.
 */
globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

globalThis.DOMRect ??= class {
  constructor(
    public x = 0,
    public y = 0,
    public width = 0,
    public height = 0,
  ) {}
  top = 0; left = 0; right = 0; bottom = 0;
  static fromRect = () => new DOMRect();
  toJSON() { return this; }
} as unknown as typeof DOMRect;

Element.prototype.scrollIntoView ??= () => {};
Element.prototype.hasPointerCapture ??= () => false;
Element.prototype.setPointerCapture ??= () => {};
Element.prototype.releasePointerCapture ??= () => {};

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
