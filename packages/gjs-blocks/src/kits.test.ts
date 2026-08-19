import { emptyTheme } from '@dcms/gjs-schema';
import { describe, expect, it } from 'vitest';
import { BLOCKS_CSS } from './blocks-css';
import {
  DEFAULT_KIT_ID,
  DESIGN_KITS,
  applyKit,
  defaultKit,
  findKit,
  matchKit,
  missingThemeTokens,
  themeCoversBlocks,
} from './kits';

describe('the kit/stylesheet contract', () => {
  /**
   * The one test that matters here. Every rule in the block stylesheet reads a
   * token, and a kit that leaves one undefined does not fail loudly — the
   * declaration is simply dropped and that block renders with no font size, no
   * radius or no colour at all. Nothing else in the suite would notice.
   */
  it.each(DESIGN_KITS.map((k) => k.id))('kit %s defines every token the stylesheet reads', (id) => {
    expect(missingThemeTokens(findKit(id)!.theme), `${id} is missing tokens`).toEqual([]);
  });

  it('reports what a theme is missing rather than only that it is', () => {
    // The pre-kit default: seven colours, two fonts, four spacing steps. This is
    // what every site seeded before design kits existed still carries, and
    // refreshing its stylesheet without a kit is what this guards.
    const legacy = {
      ...emptyTheme(),
      colors: { brand: '#2563eb', text: '#0f172a', surface: '#ffffff' },
      fonts: { body: 'system-ui', heading: 'system-ui' },
      spacing: { sm: '0.5rem', md: '1rem', lg: '2rem' },
      radius: '0.5rem',
    };
    const missing = missingThemeTokens(legacy);
    expect(themeCoversBlocks(legacy)).toBe(false);
    // Not a handful — most of the sheet. Applying the CSS alone would strip the
    // type scale, the radii and the shadows off every block on the page.
    expect(missing.length).toBeGreaterThan(30);
    expect(missing).toContain('--dcms-text-base');
    expect(missing).toContain('--dcms-shadow-sm');
    expect(missing).toContain('--dcms-container');
  });

  it('says every kit covers the stylesheet', () => {
    for (const kit of DESIGN_KITS) expect(themeCoversBlocks(kit.theme), kit.id).toBe(true);
  });

  /**
   * The other half of the same contract. A literal in the stylesheet is a value
   * no kit can reach, so it survives every design change — which is how a
   * "restyled" site ends up still showing one stubborn blue button.
   */
  it('hard-codes no colour of its own', () => {
    const structural = BLOCKS_CSS
      // Comments explain the intent and may name a colour.
      .replace(/\/\*[\s\S]*?\*\//g, '');
    expect(structural).not.toMatch(/#[0-9a-fA-F]{3,8}\b/);
    expect(structural).not.toMatch(/\b(?:rgba?|hsla?)\(/);
  });

  it('collapses explicit column counts on small screens', () => {
    // A four-column grid that stays four columns on a phone is the single most
    // visible way a generated page looks broken.
    const small = BLOCKS_CSS.slice(BLOCKS_CSS.indexOf('@media (max-width: 640px)'));
    expect(small).toContain('.dcms-grid[data-columns]');
    expect(small).toContain('grid-template-columns: minmax(0, 1fr)');
  });
});

describe('the kit catalogue', () => {
  it('offers several genuinely different looks', () => {
    expect(DESIGN_KITS.length).toBeGreaterThanOrEqual(6);
  });

  it('gives every kit a unique id', () => {
    const ids = DESIGN_KITS.map((k) => k.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('names a default that exists', () => {
    expect(findKit(DEFAULT_KIT_ID)).toBeDefined();
    expect(defaultKit().id).toBe(DEFAULT_KIT_ID);
  });

  it('loads no web font', () => {
    // A kit that reached for a font CDN would put every visitor's first paint
    // behind a third party, on a page that otherwise fetches nothing.
    for (const kit of DESIGN_KITS) {
      for (const stack of Object.values(kit.theme.fonts)) {
        expect(stack).not.toMatch(/https?:|@import|url\(/);
      }
    }
  });

  it('describes each kit for the picker', () => {
    for (const kit of DESIGN_KITS) {
      expect(kit.name).not.toBe('');
      expect(kit.description).not.toBe('');
      expect(kit.preview.swatches.length).toBeGreaterThanOrEqual(3);
    }
  });

  it('gives each kit a distinct palette and type pairing', () => {
    const looks = DESIGN_KITS.map((k) => `${k.theme.colors.brand}|${k.theme.fonts.heading}`);
    expect(new Set(looks).size).toBe(looks.length);
  });
});

describe('applyKit', () => {
  const studio = findKit('studio')!;
  const noir = findKit('noir')!;

  it('replaces every group the kit defines', () => {
    const applied = applyKit(studio.theme, noir);
    expect(applied.colors).toEqual(noir.theme.colors);
    expect(applied.fonts).toEqual(noir.theme.fonts);
    expect(applied.text).toEqual(noir.theme.text);
    expect(applied.shadows).toEqual(noir.theme.shadows);
    expect(applied.metrics).toEqual(noir.theme.metrics);
    expect(applied.radius).toBe(noir.theme.radius);
  });

  it('keeps the author’s own custom properties', () => {
    // No kit knows about these, so none should delete them.
    const theme = { ...studio.theme, custom: { '--brand-swoosh': 'url(/swoosh.svg)' } };
    expect(applyKit(theme, noir).custom).toEqual({ '--brand-swoosh': 'url(/swoosh.svg)' });
  });

  it('does not mutate the theme it was given', () => {
    const theme = { ...studio.theme, custom: { x: '1' } };
    applyKit(theme, noir);
    expect(theme.colors).toEqual(studio.theme.colors);
  });
});

describe('matchKit', () => {
  it('recognises a theme a kit produced', () => {
    for (const kit of DESIGN_KITS) {
      expect(matchKit(kit.theme)?.id).toBe(kit.id);
    }
  });

  it('still recognises the kit after an unrelated tweak', () => {
    // The picker should show what the author is on, not fall back to "Custom"
    // the moment they nudge one spacing step.
    const tweaked = applyKit(defaultKit().theme, defaultKit());
    tweaked.spacing = { ...tweaked.spacing, lg: '3rem' };
    expect(matchKit(tweaked)?.id).toBe(DEFAULT_KIT_ID);
  });

  it('returns nothing for a theme no kit produced', () => {
    expect(matchKit({ ...defaultKit().theme, colors: { brand: '#123456' } })).toBeUndefined();
  });
});
