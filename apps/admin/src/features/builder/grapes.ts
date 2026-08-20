import {
  STYLE_SECTORS,
  applyThemeSwatches,
  dcmsCore,
  dcmsFreeCanvas,
  dcmsPlugins,
  parseCssSync,
  parseHtmlSync,
} from '@dcms/gjs-blocks';
import type { ParsedCssRule, ParsedNode } from '@dcms/gjs-parse';
import type { DcmsComponentSpec, ThemeTokens } from '@dcms/gjs-schema';
import grapesjs, { type Editor, type EditorConfig } from 'grapesjs';
// GrapesJS's own stylesheet. Required even though every manager runs in
// `custom: true` mode and the panels are ours: the *canvas* is GrapesJS's own
// DOM, and this is what gives it a size (`.gjs-editor { height: 100% }`) along
// with the drag placeholder, selection badge, toolbar and resize handles.
// Without it the editor collapses to an unstyled 300x150 iframe.
import 'grapesjs/dist/css/grapes.min.css';
import './grapes.css';

/**
 * GrapesJS instance setup for the Mode A builder.
 *
 * Two things here are load-bearing:
 *
 * 1. **Worker-fed parsers.** GrapesJS accepts a custom code parser returning
 *    plain `ParsedNode[]` and a custom CSS parser returning `ParsedCssRule[]`,
 *    neither of which touches the DOM. Both hooks are wired to a memo handed
 *    over from the parse worker, so the expensive tokenizing happens off the
 *    main thread and only the cheap walk into component definitions stays on it.
 *    Both parsers must be synchronous, hence the handoff rather than a promise.
 *
 * 2. **Custom manager UIs.** BlockManager, StyleManager, TraitManager and
 *    LayerManager all run with `custom: true`, so GrapesJS keeps the state and
 *    emits `*:custom` events while the panels are rendered by our own React
 *    components in the admin design system.
 */

export const THEME_STYLE_ID = 'dcms-theme-vars';

/** Where a CSS rule came from, so a save writes it back to the right file. */
export const SOURCE_PROP = 'dcmsSource';

export interface BuilderEditorOptions {
  container: HTMLElement;
  /** Plugin ids enabled for this tenant; gates plugin-backed blocks. */
  enabledPluginIds?: Iterable<string>;
  /** Components generated from the tenant's plugin instances. */
  pluginSpecs?: readonly DcmsComponentSpec[];
  /** Theme tokens, used to seed the Style Manager's colour presets. */
  theme?: ThemeTokens;
  /** Additional GrapesJS plugins. */
  plugins?: EditorConfig['plugins'];
  pluginsOpts?: EditorConfig['pluginsOpts'];
}

/**
 * A synchronous handoff table for content already parsed in the worker.
 *
 * The parser hooks GrapesJS calls cannot await, so the flow for a page load is:
 * parse in the worker → `preload(input, result)` → call `setComponents(input)` →
 * the hook finds the exact input string and returns the ready result.
 *
 * A miss is normal, not an error. GrapesJS parses on its own account too — every
 * dropped block, every paste, every `append()` of a markup string — and none of
 * those pass through the worker first. Those inputs are small, so they are
 * parsed synchronously on the miss path with the *same* parser the worker runs,
 * which keeps a dropped block and a loaded file producing identical component
 * trees. (Returning an empty parse instead is what silently broke drag and drop:
 * every drop produced no components at all, with no error anywhere.)
 */
class ParseHandoff<T> {
  private readonly table = new Map<string, T>();

  preload(input: string, value: T): void {
    this.table.set(input, value);
  }

  take(input: string): T | undefined {
    const value = this.table.get(input);
    this.table.delete(input);
    return value;
  }

  clear(): void {
    this.table.clear();
  }
}

export const htmlHandoff = new ParseHandoff<ParsedNode[]>();
export const cssHandoff = new ParseHandoff<ParsedCssRule[]>();


export function createEditor({
  container,
  enabledPluginIds = [],
  pluginSpecs = [],
  theme,
  plugins = [],
  pluginsOpts = {},
}: BuilderEditorOptions): Editor {
  const editor = grapesjs.init({
    container,
    height: '100%',
    width: 'auto',
    fromElement: false,
    // Persistence is driven by the shared working-draft store (see storage.ts),
    // not by GrapesJS: the Mode B autosave already batches, hashes and conflict-
    // checks every write, and a second debounce on top of it would only make
    // saves land later and in a different order than the code view's.
    storageManager: false,
    // The editor chrome is ours; GrapesJS should not render panels of its own.
    panels: { defaults: [] },
    blockManager: { custom: true },
    styleManager: { custom: true, sectors: STYLE_SECTORS as never },
    traitManager: { custom: true },
    layerManager: { custom: true },
    selectorManager: { componentFirst: true },
    assetManager: { custom: true },
    undoManager: { trackSelection: false },

    deviceManager: {
      devices: [
        { id: 'desktop', name: 'Desktop', width: '' },
        { id: 'tablet', name: 'Tablet', width: '768px', widthMedia: '1024px' },
        { id: 'mobile', name: 'Mobile', width: '375px', widthMedia: '640px' },
      ],
    },

    canvas: {
      // A dropped block should be styleable immediately, so the frame gets a
      // minimal reset. The site's own reset lives in styles/global.css.
      // The scrollbar rules are builder chrome, not part of the page: the frame
      // is its own document, so the admin's scrollbar styling stops at the
      // iframe boundary and the OS default would show through. Fixed
      // translucent greys rather than theme variables — the frame resolves
      // `var(--dcms-*)` from the *site's* theme, which knows nothing about the
      // admin's light/dark mode, and a translucent grey reads correctly on both.
      frameStyle: `
        body { margin: 0; min-height: 100%; }
        [data-gjs-highlightable] { outline: 1px auto rgba(148,163,184,.45); outline-offset: -1px; }
        .gjs-dashed *[data-gjs-highlightable] { outline: 1px dashed rgba(148,163,184,.6); }
        html { scrollbar-width: thin; scrollbar-color: rgba(100,116,139,.5) transparent; }
        ::-webkit-scrollbar { width: 10px; height: 10px; }
        ::-webkit-scrollbar-track, ::-webkit-scrollbar-corner { background: transparent; }
        ::-webkit-scrollbar-thumb {
          background-color: rgba(100,116,139,.5);
          border: 3px solid transparent;
          background-clip: content-box;
          border-radius: 999px;
        }
        ::-webkit-scrollbar-thumb:hover { background-color: rgba(100,116,139,.8); }
      `,
    },

    parser: {
      // Worker handoff first, synchronous parse on a miss; see ParseHandoff.
      parsersCode: {
        worker: (input) => htmlHandoff.take(input) ?? parseHtmlSync(input),
      },
      parserCode: 'worker',
      parserCss: (input) => cssHandoff.take(input) ?? parseCssSync(input),
      optionsHtml: {
        allowScripts: false,
        allowUnsafeAttr: false,
        allowUnsafeAttrValue: false,
      },
    },

    plugins: [
      // The catalogue first: plugin-generated components extend it, and the
      // free-canvas drag mode only makes sense once its container type exists.
      (e) => dcmsCore(e, { enabledPluginIds }),
      // Generated separately, and with prefixed block ids, so a tenant instance
      // named "Hero" cannot replace the built-in Hero block.
      (e) => dcmsPlugins(e, { specs: pluginSpecs }),
      dcmsFreeCanvas,
      ...(plugins ?? []),
    ],
    pluginsOpts,
  });

  // After init, not as a plugin: the Style Manager only builds its sectors part
  // way through `init`, so a plugin runs too early to find them and would set
  // the theme swatches on nothing at all. See the note in @dcms/gjs-blocks'
  // theme module.
  applyThemeSwatches(editor, theme);

  return editor;
}

/**
 * Put the generated theme variables into the canvas iframe.
 *
 * They live in a dedicated `<style>` element rather than in the CssComposer
 * because `styles/theme.css` is regenerated from `site.json` on every save: as
 * editable rules they would look changeable in the Style Manager and then be
 * silently overwritten. This way the canvas resolves `var(--dcms-*)` exactly as
 * the published page does, and the theme stays editable only where it is real —
 * the theme panel.
 */
export function applyThemeCss(editor: Editor, css: string): void {
  const doc = editor.Canvas.getDocument();
  if (!doc) return;
  let style = doc.getElementById(THEME_STYLE_ID) as HTMLStyleElement | null;
  if (!style) {
    style = doc.createElement('style');
    style.id = THEME_STYLE_ID;
    doc.head.appendChild(style);
  }
  if (style.textContent !== css) style.textContent = css;
}
