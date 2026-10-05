/** The theme variable (and the runtime's own fallback) each background choice paints with. */
export const BACKGROUND_VARS: Readonly<Record<string, readonly [string, string]>> = {
  none: ['--dcms-color-surface', '#ffffff'],
  alt: ['--dcms-color-surface-alt', '#f8fafc'],
  soft: ['--dcms-color-brand-soft', '#eef2ff'],
  inverse: ['--dcms-color-inverse', '#0f172a'],
};

export function hasSwatches(name: string, values: readonly string[]): boolean {
  return name === 'background' && values.every((v) => v in BACKGROUND_VARS);
}
