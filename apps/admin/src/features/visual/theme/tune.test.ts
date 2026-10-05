import { DESIGN_KITS } from '@dcms/gjs-blocks';
import { themeTokensSchema } from '@dcms/site-runtime';
import { describe, expect, it } from 'vitest';
import { contrast, tuneTheme } from './tune';

const studio = DESIGN_KITS.find((k) => k.id === 'studio')!;

describe('theme tuning', () => {
  it('with nothing tuned is exactly the kit, marked with where it came from', () => {
    expect(tuneTheme(studio, {})).toEqual({ ...studio.theme, custom: {}, kit: 'studio' });
  });

  it('derives a brand colour family whose text stays readable', () => {
    const light = tuneTheme(studio, { brand: '#fde047' }).colors;
    expect(light['brand-contrast']).toBe('#111827');
    expect(contrast(light.brand!, light['brand-contrast']!)).toBeGreaterThan(4.5);
    expect(tuneTheme(studio, { brand: '#1e3a8a' }).colors['brand-contrast']).toBe('#ffffff');
  });

  it('scales corners, air and shadows from the kit, and survives the schema', () => {
    const t = tuneTheme(studio, { roundness: 0, density: 1.25, shadow: 0 });
    expect(t.radius).toBe('0rem');
    expect(t.metrics['section-py']).toBe('6.25rem');
    expect(Object.values(t.shadows).every((v) => v === 'none')).toBe(true);
    const parsed = themeTokensSchema.parse(JSON.parse(JSON.stringify(t)));
    expect(parsed.tuning).toEqual({ roundness: 0, density: 1.25, shadow: 0 });
    const soft = tuneTheme(studio, { shadow: 0.5 }).shadows;
    expect(Object.values(soft).join()).not.toEqual(Object.values(studio.theme.shadows).join());
  });
});
