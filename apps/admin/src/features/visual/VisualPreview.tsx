import {
  DataClientContext,
  SiteContext,
  SiteStyles,
  configureSiteEdge,
  guardedRoutes,
  loadDocuments,
  resetSiteAccess,
  routeTitles,
  siteRoutes,
  signOutVisitor,
  useAccount,
  type DataClient,
  type SiteAccess,
} from '@dcms/site-runtime';
import { useMutation } from '@tanstack/react-query';
import { Eraser, LogIn, LogOut, Lock, UserRound } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { useTranslation } from 'react-i18next';
import { RouterProvider, createMemoryRouter } from 'react-router';
import { toast } from 'sonner';
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { usePluginUi } from '../../pluginHost/api';
import { PreviewAddressBar, PreviewIdentityMenu, usePreviewAuth, useVfs } from '../site-source';
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
 *
 * Signed in as the site's visitors are (#269): the preview asks its own edge endpoints, follows
 * the access rules of the site's main domain, and when a page is not open to whoever it is
 * previewing as, says so over the frame and offers to sign in.
 */
export function VisualPreview({ siteId, initialPath, width }: { siteId: string; initialPath: string; width?: string }) {
  const { t } = useTranslation();
  const frame = useRef<HTMLIFrameElement>(null);
  const auth = usePreviewAuth(siteId);
  const [blocked, setBlocked] = useState<SiteAccess | null>(null);
  const root = useRef<Root | null>(null);
  // Where the preview is, as a browser shows it; a redraw (an edit, a reload, someone else to
  // preview as) stays on this page instead of going back to the one the builder opened.
  const [path, setPath] = useState(initialPath);
  const current = useRef(initialPath);
  const router = useRef<ReturnType<typeof createMemoryRouter> | null>(null);
  const [reloads, setReloads] = useState(0);
  useEffect(() => {
    current.current = initialPath;
    setPath(initialPath);
  }, [initialPath]);
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

  // The page's own sign-in and sign-out buttons open the preview's popup, as the toolbar's do.
  const { signIn, signOut } = auth;
  useEffect(() => {
    if (!auth.base) return;
    configureSiteEdge({ base: auth.base, leave: (_url, access) => setBlocked(access), signIn, signOut });
  }, [auth.base, signIn, signOut]);
  const drawnAs = useRef<number | null>(null);

  useEffect(() => {
    const doc = frame.current?.contentDocument;
    if (!doc) return;
    // Signed in, out or as someone else since the last draw: ask the rules, and who it is, afresh.
    if (drawnAs.current !== auth.version) {
      drawnAs.current = auth.version;
      resetSiteAccess();
    }
    setBlocked(null);
    if (!root.current) {
      doc.open();
      doc.write('<!doctype html><html><head><meta charset="utf-8"></head><body><div id="root"></div></body></html>');
      doc.close();
      root.current = createRoot(doc.getElementById('root')!);
      watchMedia(doc);
    }
    const next = createMemoryRouter(guardedRoutes(siteRoutes(documents, registry)).routes, { initialEntries: [current.current] });
    router.current = next;
    const stop = next.subscribe((state) => {
      const at = state.location.pathname + state.location.search + state.location.hash;
      current.current = at;
      setPath(at);
    });
    root.current.render(
      <DataClientContext.Provider value={dataClient}>
        <SiteContext.Provider value={{ app: documents.app, titles: routeTitles(documents) }}>
          <SiteStyles theme={documents.theme} />
          <RouterProvider router={next} />
        </SiteContext.Provider>
      </DataClientContext.Provider>,
    );
    return stop;
  }, [documents, initialPath, registry, dataClient, auth.version, reloads]);

  useEffect(
    () => () => {
      const r = root.current;
      root.current = null;
      if (r) queueMicrotask(() => r.unmount());
    },
    [],
  );

  return (
    <div className="flex h-full flex-col">
      <PreviewAddressBar
        path={path}
        onGo={(to) => void router.current?.navigate(to as never)}
        onReload={() => setReloads((n) => n + 1)}
      >
        {auth.identity?.available ? <PreviewIdentityMenu auth={auth} /> : null}
        <PreviewVisitorMenu siteId={siteId} dataClient={dataClient} onReset={() => setReloads((n) => n + 1)} />
      </PreviewAddressBar>
    <div className="relative flex min-h-0 flex-1 justify-center overflow-auto bg-muted/40">
      <iframe
        ref={frame}
        title="Preview"
        className="h-full border-0 bg-white shadow-sm"
        style={{ width: width || '100%' }}
      />
      {blocked && blocked !== 'allow' ? (
        <div className="absolute inset-0 flex items-center justify-center bg-background/80 p-6">
          <div role="status" className="max-w-sm space-y-3 rounded-lg border bg-card p-5 text-center shadow-sm">
            <Lock className="mx-auto h-5 w-5 text-muted-foreground" aria-hidden />
            <p className="text-sm">{t(blocked === 'signin' ? 'previewAuth.pageNeedsSignIn' : 'previewAuth.pageForbidden')}</p>
            {blocked === 'signin' ? (
              <Button size="sm" onClick={auth.signIn}>
                <LogIn className="h-4 w-4" /> {t('previewAuth.signIn')}
              </Button>
            ) : null}
          </div>
        </div>
      ) : null}
    </div>
    </div>
  );
}

/**
 * The visitor the preview is signed in as (Visitor Authentication): the page's own sign-in and
 * registration forms sign test visitors in to the preview's sandbox, never the site's real
 * accounts, so there is no "View as" here — register one. Nothing when no instance is installed.
 */
function PreviewVisitorMenu({ siteId, dataClient, onReset }: { siteId: string; dataClient: DataClient; onReset: () => void }) {
  const { t } = useTranslation();
  const ui = usePluginUi();
  const installed = !!ui.data?.find((p) => p.pluginId === 'visitor-auth')?.instances.some((i) => i.enabled);
  const visitor = useAccount('visitors', dataClient, installed);
  const reset = useMutation({
    mutationFn: () => api.post(`/admin/sites/${siteId}/preview/sandbox/reset`),
    onSuccess: () => {
      signOutVisitor();
      onReset();
      toast.success(t('previewAuth.sandboxReset'));
    },
    onError: () => toast.error(t('errors.generic')),
  });
  if (!installed) return null;
  const label = visitor ? t('previewAuth.visitorAs', { name: visitor.name ?? visitor.email ?? '' }) : t('previewAuth.noVisitor');
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="sm" className="h-6 min-w-0 gap-1 px-1.5 text-xs" aria-label={t('previewAuth.visitorMenu')}>
          <UserRound className="h-3.5 w-3.5 shrink-0" />
          <span className="min-w-0 truncate">{label}</span>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuLabel className="max-w-64 text-xs font-normal text-muted-foreground">{t('previewAuth.visitorHint')}</DropdownMenuLabel>
        <DropdownMenuSeparator />
        {visitor ? (
          <DropdownMenuItem onSelect={signOutVisitor}>
            <LogOut className="h-4 w-4" /> {t('previewAuth.visitorSignOut')}
          </DropdownMenuItem>
        ) : null}
        <DropdownMenuItem disabled={reset.isPending} onSelect={() => reset.mutate()}>
          <Eraser className="h-4 w-4" /> {t('previewAuth.resetSandbox')}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
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
