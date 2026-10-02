import { RUNTIME_CSS, themeRootCss, type Registry, type ThemeTokens } from '@dcms/site-runtime';
import grapesjs, { type Editor } from 'grapesjs';
// GrapesJS's own stylesheet sizes the canvas and draws the selection chrome; the second file
// hands the whole element to the canvas, since every manager's UI is ours. See the notes in
// both, and in builder/grapes.ts.
import 'grapesjs/dist/css/grapes.min.css';
import '../../builder/grapes.css';
import { registerVisualTypes } from './types';

export const VISUAL_DEVICES = [
  { id: 'desktop', name: 'Desktop', width: '' },
  { id: 'tablet', name: 'Tablet', width: '768px', widthMedia: '1024px' },
  { id: 'mobile', name: 'Mobile', width: '375px', widthMedia: '640px' },
] as const;

export type DeviceId = (typeof VISUAL_DEVICES)[number]['id'];

const THEME_STYLE_ID = 'dcms-theme';

/**
 * Editor-only chrome. An empty slot is a zero-height box, which would leave nothing to drop
 * onto; the outline is what tells the author a slot is there. Never shipped to the site.
 */
const EDIT_CSS = `
html, body { min-height: 100%; }
.dcms-slot:empty { min-height: 3rem; outline: 1px dashed rgba(100,116,139,.55); outline-offset: -2px; }
.dcms-slot:empty::before { content: ''; display: block; min-height: 3rem; }
html { scrollbar-width: thin; scrollbar-color: rgba(100,116,139,.5) transparent; }
`;

/**
 * GrapesJS for the Mode D builder.
 *
 * Nothing is parsed from HTML and nothing is persisted by GrapesJS: pages arrive as node trees
 * through `setComponents` (see tree.ts) and leave the same way, so the parser and storage
 * managers are out of the picture entirely.
 */
export function createVisualEditor(container: HTMLElement, registry: Registry): Editor {
  const editor = grapesjs.init({
    container,
    height: '100%',
    width: 'auto',
    fromElement: false,
    // On by default since 0.22: posts usage data from the author's browser on every load.
    telemetry: false,
    storageManager: false,
    panels: { defaults: [] },
    blockManager: { custom: true },
    layerManager: { custom: true },
    traitManager: { custom: true },
    styleManager: { custom: true },
    selectorManager: { componentFirst: true },
    assetManager: { custom: true },
    undoManager: { trackSelection: false },
    deviceManager: { devices: VISUAL_DEVICES.map((d) => ({ ...d })) },
    canvas: { frameStyle: EDIT_CSS + RUNTIME_CSS },
    parser: { optionsHtml: { allowScripts: false, allowUnsafeAttr: false, allowUnsafeAttrValue: false } },
    plugins: [(e) => registerVisualTypes(e, registry)],
  });

  // The page's root is the only thing directly in the body; everything else goes into a slot.
  editor.getWrapper()?.set({ droppable: false, selectable: false, hoverable: false });
  return editor;
}

/** Put the site's theme variables into the canvas frame, where the components read them. */
export function applyTheme(editor: Editor, theme: ThemeTokens): void {
  const doc = editor.Canvas.getDocument();
  if (!doc) return;
  let style = doc.getElementById(THEME_STYLE_ID) as HTMLStyleElement | null;
  if (!style) {
    style = doc.createElement('style');
    style.id = THEME_STYLE_ID;
    doc.head.appendChild(style);
  }
  const css = themeRootCss(theme);
  if (style.textContent !== css) style.textContent = css;
}
