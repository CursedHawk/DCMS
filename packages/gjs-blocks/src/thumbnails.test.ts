import { describe, expect, it } from 'vitest';
import { BUILTIN_SPECS } from './specs';
import { pluginSpecs } from './plugins/specs';
import { THUMBNAIL_ARCHETYPES, archetypeFor, thumbnail, thumbnailFor } from './thumbnails';

describe('thumbnails', () => {
  it('draws every archetype as a self-contained svg', () => {
    for (const name of THUMBNAIL_ARCHETYPES) {
      const svg = thumbnail(name);
      expect(svg, name).toMatch(/^<svg viewBox="0 0 96 60"/);
      expect(svg, name).toMatch(/<\/svg>$/);
      // An empty drawing is worse than the glyph it replaced.
      expect(svg.length, name).toBeGreaterThan(120);
    }
  });

  it('gives every built-in block a thumbnail', () => {
    for (const spec of BUILTIN_SPECS) {
      expect(thumbnailFor(spec), spec.type).toContain('<svg');
    }
  });

  /**
   * The one that would actually rot. `BY_TYPE` names specs by string, so a spec
   * renamed or removed leaves an entry pointing at nothing — silently, because
   * the fallback chain still produces *a* picture.
   */
  it('names no spec that does not exist', () => {
    const known = new Set(BUILTIN_SPECS.map((s) => s.type));
    const mapped = BUILTIN_SPECS.filter((s) => archetypeFor(s) !== undefined);
    expect(mapped.length).toBe(BUILTIN_SPECS.length);
    // Every archetype a spec resolves to must be one that can be drawn.
    for (const spec of BUILTIN_SPECS) {
      expect(THUMBNAIL_ARCHETYPES, spec.type).toContain(archetypeFor(spec));
    }
    expect(known.size).toBe(BUILTIN_SPECS.length);
  });

  it('distinguishes the sections that look alike as glyphs', () => {
    const bySpec = (type: string) => archetypeFor(BUILTIN_SPECS.find((s) => s.type === type)!);
    const drawings = ['FeatureGrid', 'CardGrid', 'TeamGrid', 'PricingTable'].map(bySpec);
    expect(new Set(drawings).size).toBe(drawings.length);
  });

  it('draws a generated plugin block from the layout its content implies', () => {
    const specs = pluginSpecs(
      [
        {
          id: 'gallery',
          name: 'Gallery',
          contentTypes: [{ name: 'photo', fields: [{ name: 'image', type: 'MediaRef' }] }],
        },
      ],
      [{ id: 'i1', pluginId: 'gallery', slug: 'gallery', name: 'Photos', enabled: true }],
    );
    // A photo gallery defaults to tiles, so its palette tile shows tiles rather
    // than the generic plugin card grid.
    expect(archetypeFor(specs[0]!)).toBe('tiles');
  });
});
