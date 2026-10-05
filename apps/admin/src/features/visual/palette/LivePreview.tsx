import {
  DataClientContext,
  RUNTIME_CSS,
  RenderModeContext,
  RenderNode,
  SiteContext,
  THEME_JSON,
  themeRootCss,
  themeTokensSchema,
  type App,
  type DataClient,
  type Node,
  type Registry,
} from '@dcms/site-runtime';
import { useEffect, useRef } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { useVfs } from '../../site-source/vfs';

/** Previews show what a component looks like, not live content: every list is empty. */
const NO_DATA: DataClient = { get: async () => ({ items: [], totalCount: 0 }), post: async () => undefined };

/** The width a preview is laid out at before it is scaled down to fit its card. */
const LAYOUT_WIDTH = 760;

/**
 * A miniature of a node tree, drawn by the site runtime in this site's theme — what dropping it
 * will look like. Rendered into a shadow root so the runtime's stylesheet stays inside it, and
 * as `live` so editor notes do not show — section templates draw as `edit`, so a picture still
 * to be chosen shows as a placeholder rather than nothing. Trusted code only: the tree is data
 * the runtime draws (tenant components, section templates); developer components are
 * placeholders here as on the canvas.
 */
export function LivePreview({
  node,
  registry,
  app,
  mode = 'live',
  layoutWidth = LAYOUT_WIDTH,
}: {
  node: Node;
  registry: Registry;
  app: App | null;
  mode?: 'live' | 'edit';
  /** The width it is laid out at before being scaled to fit — a phone's, to see it as on a phone. */
  layoutWidth?: number;
}) {
  const host = useRef<HTMLDivElement>(null);
  const root = useRef<Root | null>(null);
  const frame = useRef<HTMLDivElement | null>(null);
  const themeText = useVfs((s) => s.files[THEME_JSON]);

  useEffect(() => {
    const el = host.current;
    if (!el) return;
    const shadow = el.shadowRoot ?? el.attachShadow({ mode: 'open' });
    if (!root.current) {
      const style = document.createElement('style');
      style.dataset.role = 'theme';
      const container = document.createElement('div');
      container.style.cssText = `width:${layoutWidth}px;transform-origin:top left;background:var(--dcms-color-surface,#fff);pointer-events:none;`;
      container.dataset.width = String(layoutWidth);
      shadow.append(style, container);
      frame.current = container;
      root.current = createRoot(container);
      // Scale the layout width down to the card's.
      const fit = () => {
        container.style.transform = `scale(${el.clientWidth / Number(container.dataset.width)})`;
      };
      fit();
      new ResizeObserver(fit).observe(el);
    }
    if (frame.current && frame.current.dataset.width !== String(layoutWidth)) {
      frame.current.dataset.width = String(layoutWidth);
      frame.current.style.width = `${layoutWidth}px`;
      frame.current.style.transform = `scale(${el.clientWidth / layoutWidth})`;
    }
    let theme = '';
    try {
      const parsed = themeTokensSchema.safeParse(JSON.parse(themeText ?? '{}'));
      if (parsed.success) theme = themeRootCss(parsed.data).replace(':root', ':host');
    } catch {
      // A broken theme file is the Problems panel's to report; previews fall back to defaults.
    }
    (shadow.querySelector('style[data-role="theme"]') as HTMLStyleElement).textContent = RUNTIME_CSS + theme;
    root.current.render(
      <RenderModeContext.Provider value={mode}>
        <SiteContext.Provider value={{ app }}>
          <DataClientContext.Provider value={NO_DATA}>
            <RenderNode node={node} registry={registry} />
          </DataClientContext.Provider>
        </SiteContext.Provider>
      </RenderModeContext.Provider>,
    );
  }, [node, registry, app, themeText, mode, layoutWidth]);

  useEffect(
    () => () => {
      const r = root.current;
      root.current = null;
      if (r) queueMicrotask(() => r.unmount());
    },
    [],
  );

  return <div ref={host} aria-hidden className="absolute inset-0 overflow-hidden" />;
}
