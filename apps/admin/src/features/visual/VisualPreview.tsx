import { DataClientContext, SiteContext, SiteStyles, loadDocuments, routeTitles, siteRoutes } from '@dcms/site-runtime';
import { useEffect, useMemo, useRef } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { RouterProvider, createMemoryRouter } from 'react-router';
import { useVfs } from '../site-source';
import { assetIdOf, resolveMedia } from '../builder/panels/media';
import { useVisual } from './store';

/**
 * The site as a visitor gets it: the same `siteRoutes` the published app builds, under a memory
 * router, with live navigation and actions. `RenderMode` is `live` here on purpose — a preview
 * that behaved differently from the site would be a preview that lies.
 *
 * It renders into a frame of its own: the runtime's stylesheet styles `html` and `body`, which
 * must not reach the admin, and the frame's width is the viewport the responsive rules see. The
 * frame gets its own React root because React's event delegation does not cross into another
 * document through a portal.
 *
 * Only DCMS's own runtime code runs here (ADR 0020): the documents are data.
 */
export function VisualPreview({ initialPath, width }: { initialPath: string; width?: string }) {
  const frame = useRef<HTMLIFrameElement>(null);
  const root = useRef<Root | null>(null);
  const dcmsFiles = useVfs((s) => s.files);
  const registry = useVisual((s) => s.registry);
  const dataClient = useVisual((s) => s.dataClient);

  const documents = useMemo(() => {
    const modules: Record<string, unknown> = {};
    for (const [path, text] of Object.entries(dcmsFiles)) {
      if (!path.startsWith('dcms/') || !path.endsWith('.json')) continue;
      try {
        modules[path] = JSON.parse(text);
      } catch {
        // Reported by the problems list; the preview shows what does parse.
      }
    }
    return loadDocuments(modules);
  }, [dcmsFiles]);

  useEffect(() => {
    const doc = frame.current?.contentDocument;
    if (!doc) return;
    if (!root.current) {
      doc.open();
      doc.write('<!doctype html><html><head><meta charset="utf-8"></head><body><div id="root"></div></body></html>');
      doc.close();
      root.current = createRoot(doc.getElementById('root')!);
      watchMedia(doc);
    }
    const router = createMemoryRouter(siteRoutes(documents, registry), { initialEntries: [initialPath] });
    root.current.render(
      <DataClientContext.Provider value={dataClient}>
        <SiteContext.Provider value={{ app: documents.app, titles: routeTitles(documents) }}>
          <SiteStyles theme={documents.theme} />
          <RouterProvider router={router} />
        </SiteContext.Provider>
      </DataClientContext.Provider>,
    );
  }, [documents, initialPath, registry, dataClient]);

  useEffect(
    () => () => {
      const r = root.current;
      root.current = null;
      if (r) queueMicrotask(() => r.unmount());
    },
    [],
  );

  return (
    <div className="flex h-full justify-center overflow-auto bg-muted/40">
      <iframe
        ref={frame}
        title="Preview"
        className="h-full border-0 bg-white shadow-sm"
        style={{ width: width || '100%' }}
      />
    </div>
  );
}

/**
 * Published media URLs need the admin's credentials to load here (see builder/panels/media.ts):
 * swap in the authenticated copy as images appear, without touching what is rendered from.
 */
function watchMedia(doc: Document): void {
  const swap = () => {
    for (const img of Array.from(doc.querySelectorAll<HTMLImageElement>('img[src^="/api/media/"]'))) {
      const src = img.getAttribute('src')!;
      if (!assetIdOf(src)) continue;
      // The WebP ladder's addresses do not load here either, and a srcset wins over src.
      img.removeAttribute('srcset');
      void resolveMedia(src).then((url) => {
        if (img.getAttribute('src') === src) img.setAttribute('src', url);
      });
    }
  };
  new MutationObserver(swap).observe(doc.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['src'] });
}
