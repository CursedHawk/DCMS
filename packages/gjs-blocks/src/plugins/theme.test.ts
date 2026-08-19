// @vitest-environment jsdom
import type { ThemeTokens } from '@dcms/gjs-schema';
import grapesjs, { type Editor } from 'grapesjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { STYLE_SECTORS, applyThemeSwatches } from './theme';

/**
 * The theme → Style Manager bridge, against a real editor.
 *
 * This exists because the bridge failed *silently*: `getProperty` looks sectors
 * up by id, the code passed their display names ('Typography'), and the only
 * symptom was a console warning nobody reads and a colour picker with no theme
 * swatches in it. Asserting against a real StyleManager is the only way to catch
 * that — a unit test of our own data would have passed happily.
 */

const theme: ThemeTokens = {
  colors: { brand: '#2563eb', text: '#0f172a', surface: '#ffffff' },
  fonts: { body: 'system-ui', heading: 'system-ui' },
  spacing: { md: '1rem' },
  text: {},
  shadows: {},
  metrics: {},
  radius: '0.5rem',
  custom: {},
};

let editor: Editor;

/**
 * Built the way the builder builds it — `custom: true` included.
 *
 * That flag matters more than it looks: it is what makes the Style Manager skip
 * its own UI, and the first version of this suite omitted it and passed against
 * a configuration production never uses.
 */
function makeEditor(plugins: ((e: Editor) => void)[] = []): Editor {
  const container = document.createElement('div');
  document.body.appendChild(container);
  return grapesjs.init({
    container,
    headless: true,
    storageManager: false,
    panels: { defaults: [] },
    styleManager: { custom: true, sectors: STYLE_SECTORS as never },
    plugins,
  });
}

beforeEach(() => {
  editor = makeEditor();
});

afterEach(() => editor.destroy());

/** The option ids offered for a style property, or null if it has none. */
const optionsFor = (sector: string, property: string) => {
  const model = editor.StyleManager.getProperty(sector, property);
  if (!model) return null;
  const options = model.get('options' as never) as { id: string }[] | undefined;
  return options?.map((o) => o.id) ?? null;
};

describe('STYLE_SECTORS', () => {
  it('declares every sector with a distinct id', () => {
    const ids = STYLE_SECTORS.map((s) => s.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('registers every declared sector with the editor', () => {
    for (const sector of STYLE_SECTORS) {
      expect(editor.StyleManager.getSector(sector.id), sector.id).toBeTruthy();
    }
  });

  it('is addressed by id, not by display name', () => {
    // The exact mistake that broke the swatches.
    expect(editor.StyleManager.getSector('typography')).toBeTruthy();
    expect(editor.StyleManager.getSector('Typography')).toBeFalsy();
  });

  it('puts the colour properties where applyThemeSwatches looks for them', () => {
    expect(editor.StyleManager.getProperty('typography', 'color')).toBeTruthy();
    expect(editor.StyleManager.getProperty('decorations', 'background-color')).toBeTruthy();
  });
});

describe('applyThemeSwatches', () => {
  it('offers every theme colour as a var() preset', () => {
    applyThemeSwatches(editor, theme);
    expect(optionsFor('typography', 'color')).toEqual([
      'var(--dcms-color-brand)',
      'var(--dcms-color-text)',
      'var(--dcms-color-surface)',
    ]);
  });

  it('reaches properties in the decorations sector too', () => {
    applyThemeSwatches(editor, theme);
    expect(optionsFor('decorations', 'background-color')).toContain('var(--dcms-color-brand)');
  });

  it('labels swatches in words rather than variable names', () => {
    applyThemeSwatches(editor, theme);
    const model = editor.StyleManager.getProperty('typography', 'color')!;
    const options = model.get('options' as never) as { label: string }[];
    expect(options.map((o) => o.label)).toContain('Brand');
  });

  it('carries the resolved colour so the swatch can be painted', () => {
    applyThemeSwatches(editor, theme);
    const model = editor.StyleManager.getProperty('typography', 'color')!;
    const options = model.get('options' as never) as { value: string }[];
    expect(options[0]!.value).toBe('#2563eb');
  });

  it('does nothing without a theme', () => {
    applyThemeSwatches(editor, undefined);
    expect(optionsFor('typography', 'color')).toBeNull();
  });

  it('does nothing for a theme with no colours', () => {
    applyThemeSwatches(editor, { ...theme, colors: {} });
    expect(optionsFor('typography', 'color')).toBeNull();
  });

  it('replaces the previous palette when the theme changes', () => {
    applyThemeSwatches(editor, theme);
    applyThemeSwatches(editor, { ...theme, colors: { accent: '#f00' } });
    expect(optionsFor('typography', 'color')).toEqual(['var(--dcms-color-accent)']);
  });
});

describe('when the sectors exist', () => {
  /**
   * The ordering fact the whole design rests on, pinned so nobody moves the
   * swatch call back into a plugin.
   *
   * The Style Manager builds its sectors in its own `onLoad`, which runs part
   * way through `grapesjs.init` — after the plugins. Applying the swatches from
   * a plugin therefore found no sectors and did nothing, and the only symptom
   * was a console warning and colour pickers with no theme presets.
   */
  it('not while plugins run, but as soon as init returns', () => {
    editor.destroy();
    let duringPlugin: boolean | null = null;
    editor = makeEditor([
      (e) => {
        duringPlugin = !!e.StyleManager.getSector('typography');
      },
    ]);

    expect(duringPlugin, 'sectors must not be expected at plugin time').toBe(false);
    expect(!!editor.StyleManager.getSector('typography')).toBe(true);
  });

  /**
   * And why `editor.onReady` is not the escape hatch it looks like: `ready` also
   * waits on the canvas frame, which a headless editor never loads. A callback
   * parked there would never run for tests or any non-browser consumer.
   */
  it('with `load` never firing for a headless editor', () => {
    editor.destroy();
    let readyFired = false;
    editor = makeEditor([(e) => e.onReady(() => (readyFired = true))]);
    expect(readyFired).toBe(false);
  });

  it('so applying after init reaches a real sector', () => {
    editor.destroy();
    editor = makeEditor();
    applyThemeSwatches(editor, theme);
    expect(optionsFor('typography', 'color')).toContain('var(--dcms-color-brand)');
  });
});
