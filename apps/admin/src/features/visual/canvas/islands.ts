import { useEffect, useRef } from 'react';
import type { BuildRequest, BuildResponse } from '../../ide/preview/bundler.worker';
import BundlerWorker from '../../ide/preview/bundler.worker?worker';
import { previewVersions } from '../../ide/preview/usePreview';
import { useVfs } from '../../site-source/vfs';
import { useVisual } from '../store';

/**
 * Developer components on the canvas, without running them in the admin (ADR 0020 amendment).
 *
 * <p>The canvas is same-origin with the admin, so a developer's TSX must never execute in it.
 * Instead the site's components are bundled — by the same in-browser esbuild the Mode B preview
 * uses — into one small page, and each developer component on the canvas is a frame of that page
 * with `sandbox="allow-scripts"` and nothing else: an opaque origin that can reach neither the
 * canvas, the admin, nor the admin's cookies and storage. The frame is told which component and
 * which props to draw, and answers with its height; that is the whole conversation
 * (CodeIsland.tsx). One build serves every island and is redone when the site's files change.</p>
 *
 * <p>The entry exists only in what is sent to the bundler: it is never saved to the site, never
 * committed, never deployed.</p>
 */

export const ISLAND_ENTRY = 'src/__dcms_island.tsx';

export const ISLAND_SOURCE = `import { Component, StrictMode, useEffect, useState, type ComponentType, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import { SiteStyles, codeSourceOf, loadDocuments } from './dcms/runtime';

const documents = loadDocuments(import.meta.glob('../dcms/**/*.json', { eager: true, import: 'default' }));
const modules = import.meta.glob('./components/*.tsx', { eager: true, import: 'default' }) as Record<string, unknown>;
const byName = new Map<string, ComponentType<Record<string, unknown>>>();
for (const [key, value] of Object.entries(modules)) {
  const name = codeSourceOf(key);
  if (name && typeof value === 'function') byName.set(name, value as ComponentType<Record<string, unknown>>);
}

const tell = (message: Record<string, unknown>) => window.parent.postMessage({ __dcms: 'island', ...message }, '*');

class Boundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() {
    return { failed: true };
  }
  componentDidCatch(error: unknown) {
    tell({ kind: 'error', message: String((error as Error)?.message ?? error) });
  }
  render() {
    return this.state.failed ? null : this.props.children;
  }
}

function Island() {
  const [draw, setDraw] = useState<{ name: string; props: Record<string, unknown> } | null>(null);
  useEffect(() => {
    const onMessage = (event: MessageEvent) => {
      // The canvas draws through the admin's own code, so a message may come from the admin
      // window rather than the canvas frame. Either may only tell the island what to draw.
      if (event.source !== window.parent && event.source !== window.top) return;
      const data = event.data as { __dcms?: string; name?: unknown; props?: unknown } | null;
      if (data?.__dcms === 'island-draw' && typeof data.name === 'string') {
        setDraw({ name: data.name, props: data.props && typeof data.props === 'object' ? (data.props as Record<string, unknown>) : {} });
      }
    };
    window.addEventListener('message', onMessage);
    tell({ kind: 'ready' });
    return () => window.removeEventListener('message', onMessage);
  }, []);
  if (!draw) return null;
  const Impl = byName.get(draw.name);
  if (!Impl) {
    tell({ kind: 'error', message: 'src/components/' + draw.name + '.tsx has no default export.' });
    return null;
  }
  return <Boundary key={draw.name}><Impl {...draw.props} /></Boundary>;
}

window.addEventListener('error', (event) => tell({ kind: 'error', message: String(event.message) }));
new ResizeObserver(() => tell({ kind: 'size', height: Math.ceil(document.body.getBoundingClientRect().height) })).observe(document.body);
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <SiteStyles theme={documents.theme} />
    <Island />
  </StrictMode>,
);
`;

const TAILWIND_BROWSER_CDN = 'https://unpkg.com/@tailwindcss/browser@4';

/**
 * The island page. No preview bridge: the Mode B bridge proxies `fetch` through the parent with
 * the admin's credentials, and an island is not given that — a component that fetches its own
 * data shows it in the full preview and on the site.
 */
export function islandShell(js: string, css: string, usesTailwind: boolean): string {
  const styles = usesTailwind
    ? `<style type="text/tailwindcss">${css}</style><script src="${TAILWIND_BROWSER_CDN}"></script>`
    : `<style>${css}</style>`;
  return `<!doctype html>
<html>
  <head>
    <meta charset="utf-8" />
    <style>html,body{margin:0;background:transparent}</style>
    ${styles}
  </head>
  <body>
    <div id="root"></div>
    <script type="module">${js}</script>
  </body>
</html>`;
}

const DEBOUNCE_MS = 1500;

/** Keeps the island page built while the site has developer components; nothing otherwise. */
export function useIslandBuild(enabled: boolean, siteId: string): void {
  const rev = useVfs((s) => s.rev);
  const loaded = useVfs((s) => s.loaded);
  const worker = useRef<Worker | null>(null);
  const request = useRef(0);
  const first = useRef(true);

  useEffect(() => {
    if (!enabled) return;
    const w = (worker.current ??= new BundlerWorker());
    const onMessage = (e: MessageEvent<BuildResponse>) => {
      if (e.data.id !== request.current) return;
      const { island } = useVisual.getState();
      useVisual.getState().setIsland(
        e.data.ok
          ? { srcdoc: islandShell(e.data.js ?? '', e.data.css ?? '', e.data.usesTailwind ?? false), building: false, error: null }
          : // A failed build keeps the islands' last good page: the placeholder would hide the
            // component the author is fixing.
            { srcdoc: island.srcdoc, building: false, error: e.data.error ?? 'The developer components could not be built.' },
      );
    };
    w.addEventListener('message', onMessage);
    return () => w.removeEventListener('message', onMessage);
  }, [enabled]);

  useEffect(
    () => () => {
      worker.current?.terminate();
      worker.current = null;
    },
    [],
  );

  useEffect(() => {
    if (!enabled || !loaded) return;
    const build = () => {
      const files = { ...useVfs.getState().snapshot(), [ISLAND_ENTRY]: ISLAND_SOURCE };
      const req: BuildRequest = {
        id: ++request.current,
        files,
        entry: ISLAND_ENTRY,
        versions: previewVersions(files),
        previewBaseUrl: `/api/admin/sites/${siteId}/preview`,
      };
      useVisual.getState().setIsland({ ...useVisual.getState().island, building: true });
      worker.current?.postMessage(req);
    };
    if (first.current) {
      first.current = false;
      build();
      return;
    }
    const handle = setTimeout(build, DEBOUNCE_MS);
    return () => clearTimeout(handle);
  }, [enabled, loaded, rev, siteId]);
}
