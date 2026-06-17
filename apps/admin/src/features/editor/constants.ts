import type { Breakpoint } from '@dcms/editor-core';

/** Canvas widths per breakpoint (px) used by the editor stage and device frames. */
export const BREAKPOINT_WIDTH: Record<Breakpoint, number> = {
  desktop: 1200,
  tablet: 768,
  mobile: 390,
};

export const BREAKPOINTS: Breakpoint[] = ['desktop', 'tablet', 'mobile'];

/** Default box size when a component is dropped onto the canvas. */
export const DEFAULT_SIZE: Record<string, { w: number; h: number }> = {
  Hero: { w: 1120, h: 320 },
  Section: { w: 1120, h: 240 },
  Stack: { w: 360, h: 240 },
  Grid: { w: 720, h: 320 },
  Heading: { w: 480, h: 56 },
  Text: { w: 480, h: 96 },
  Image: { w: 360, h: 240 },
  Button: { w: 160, h: 44 },
};

export function defaultSize(type: string): { w: number; h: number } {
  return DEFAULT_SIZE[type] ?? { w: 280, h: 140 };
}

/** Snap threshold in px when dragging/resizing near guides. */
export const SNAP_THRESHOLD = 6;
