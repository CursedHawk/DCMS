import { AlertTriangle } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { renewSilently } from '../../auth';
import { adminHeaders } from '../../tenants';
import { PreviewAddressBar } from '../site-source/PreviewAddressBar';
import { PreviewIdentityMenu, PreviewVisitorMenu, usePreviewAuth } from '../site-source/previewAuth';
import { attachPreview, previewGo, type PreviewFetchProxy } from './preview/previewBridge';
import { previewEdgeTarget, previewProxyTarget } from './preview/proxyTarget';
import type { PreviewControls } from './preview/usePreview';

// Live client-side preview: the esbuild-wasm worker bundles the project and we
// render the result in a sandboxed iframe. Build errors surface as an overlay.
//
// The build itself is driven by IdePage, not here. Two callers of usePreview would be two
// workers building the same project, and the Problems view needs the same build's messages.

/**
 * What each site's sandboxed preview keeps in the storage it is lent (`__dcmsPreviewStorage` — an
 * opaque-origin frame has none): the signed-in visitor. Per site, for as long as the console page
 * is open, so a rebuild — or going to the canvas and back — stays signed in.
 */
const lentStores = new Map<string, Record<string, string>>();

/** Who a preview's visitor is, from the access token it holds (Visitor Authentication's carries the email). */
function visitorOfToken(session: string | null | undefined): { email: string } | null {
  try {
    const token = (JSON.parse(session ?? 'null') as { accessToken?: string } | null)?.accessToken;
    if (!token) return null;
    const claims = JSON.parse(atob(token.split('.')[1]!.replace(/-/g, '+').replace(/_/g, '/'))) as { email?: unknown };
    return { email: typeof claims.email === 'string' ? claims.email : '' };
  } catch {
    return null;
  }
}

export function PreviewPane({ preview, siteId }: { preview: PreviewControls; siteId: string }) {
  const { t } = useTranslation();
  const { srcdoc, error, building, refresh } = preview;
  const frameRef = useRef<HTMLIFrameElement>(null);

  /*
   * The address bar. The app's router reports where it is; a rebuild or a reload starts the
   * new document on that same page (window.__dcmsPreviewPath, read by the router shim), the way
   * a browser's reload stays where it was. Reload also remounts the frame, so it is a real
   * reload even when the build came out the same.
   */
  const [path, setPath] = useState('/');
  const pathRef = useRef('/');
  const onNav = useCallback((at: string) => {
    pathRef.current = at;
    setPath(at);
  }, []);
  const [reloads, setReloads] = useState(0);
  const [lent, setLent] = useState<Record<string, string>>(() => lentStores.get(siteId) ?? {});
  const lentRef = useRef(lent);
  const onStore = useCallback(
    (key: string, value: string | null) => {
      const next = { ...lentRef.current };
      if (value === null) delete next[key];
      else next[key] = value;
      lentRef.current = next;
      lentStores.set(siteId, next);
      setLent(next);
    },
    [siteId],
  );
  const doc = useMemo(
    () =>
      srcdoc?.replace(
        '<head>',
        // Escaped so a path (or a stored value) can never close the script it is written into.
        `<head><script>window.__dcmsPreviewPath=${JSON.stringify(pathRef.current).replace(/</g, '\\u003c')};window.__dcmsPreviewStore=${JSON.stringify(lentRef.current).replace(/</g, '\\u003c')}</script>`,
      ) ?? null,
    // Re-read on every new build and every reload; the path is taken as it is at that moment.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [srcdoc, reloads],
  );
  // Who the preview is on the site (#269): its edge questions and its sign-in go here.
  const auth = usePreviewAuth(siteId);
  const edgeBase = auth.base;

  /*
   * Serve the sandboxed preview's API calls from the admin session.
   *
   * The iframe is opaque-origin (SEC-10), so it cannot reach the DCMS API itself — no origin to
   * resolve a relative /api/... against and no token for the site:edit preview proxy. It hands us
   * the request; we replay it with the admin bearer + tenant header and hand back the response.
   *
   * The one thing this MUST NOT become is a confused deputy: the preview runs tenant- and
   * CDN-authored code, so it is only allowed to reach its own site's preview subtree. Anything
   * else is a 403 here, never a call carrying the admin token to another endpoint. The backend
   * proxy is itself site:edit-gated and tenant-scoped (SEC-14); this is the matching client guard.
   */
  const proxyFetch = useCallback<PreviewFetchProxy>(
    async (req) => {
      // The site asking its edge who is signed in or what they may open: the preview's sign-in
      // answers instead, with this console's cookies (no token involved).
      const edge = edgeBase ? previewEdgeTarget(req.url, edgeBase, window.location.origin) : null;
      if (edge !== null) {
        const res = await fetch(edge, { credentials: 'same-origin', headers: { Accept: 'application/json' } });
        return { status: res.status, statusText: res.statusText, headers: { 'content-type': res.headers.get('content-type') ?? 'application/json' }, body: await res.arrayBuffer() };
      }
      // Confused-deputy guard: normalise the attacker-controlled URL and confirm it stays inside
      // this site's own preview subtree before replaying it with the admin token. See
      // previewProxyTarget — a raw startsWith() would let `${prefix}../../tenants` reach another
      // admin route. We fetch the returned path, never req.url.
      const prefix = `/api/admin/sites/${siteId}/preview/`;
      const target = previewProxyTarget(req.url, prefix, window.location.origin);
      if (target === null) {
        return { status: 403, statusText: 'Forbidden', headers: {}, body: '' };
      }
      const send = async () => {
        const headers: Record<string, string> = { ...(await adminHeaders()) };
        const header = (name: string) => Object.entries(req.headers).find(([k]) => k.toLowerCase() === name)?.[1];
        const contentType = header('content-type');
        if (contentType) headers['Content-Type'] = contentType;
        // A write's own key, so a retried write is the same write (ADR 0024; content-api refuses a POST without one).
        const idempotency = header('idempotency-key');
        if (idempotency) headers['Idempotency-Key'] = idempotency;
        // The visitor the page signed in (Visitor Authentication, in the sandbox): Authorization here is the
        // console's, so their bearer goes as the header the preview proxy forwards in its place.
        const visitor = /^Bearer ([\w.-]+)$/.exec(header('authorization') ?? '')?.[1];
        if (visitor) headers['X-Dcms-Visitor-Token'] = visitor;
        return fetch(target, {
          method: req.method,
          headers,
          body: req.body ?? undefined,
        });
      };
      // One silent renew + replay on 401, mirroring the app's own API client: the token can go
      // stale between builds, and a dead preview is a worse signal than a slightly slow one.
      let res = await send();
      if (res.status === 401) {
        try {
          if (await renewSilently()) res = await send();
        } catch {
          /* renewal failed; let the 401 surface to the preview as-is */
        }
      }
      const headers: Record<string, string> = {};
      const responseType = res.headers.get('content-type');
      if (responseType) headers['content-type'] = responseType;
      return { status: res.status, statusText: res.statusText, headers, body: await res.arrayBuffer() };
    },
    [siteId, edgeBase],
  );

  // The site leaving for its sign-in, sign-out or a page it may not open: a popup for the first
  // two, and a word for the third — the preview cannot follow the site off its own frame.
  const { signIn, signOut } = auth;
  const onEdgeNav = useCallback(
    (url: string) => {
      if (url.startsWith('/.edge/site/signin')) signIn();
      else if (url.startsWith('/.edge/site/signout')) signOut();
      else toast.info(t('previewAuth.pageForbidden'));
    },
    [signIn, signOut, t],
  );

  // The visitor signed out from the toolbar, or the sandbox reset: forget them, and draw the page again.
  const signOutVisitor = useCallback(() => {
    onStore('dcms.visitor', null);
    setReloads((n) => n + 1);
  }, [onStore]);

  // Signed in, out or as someone else: draw the page again as them.
  const version = useRef(auth.version);
  useEffect(() => {
    if (version.current === auth.version) return;
    version.current = auth.version;
    refresh();
  }, [auth.version, refresh]);

  /*
   * Connect the running page to the agent's preview tools and the API proxy above.
   *
   * Re-attached on every `srcdoc` change because a rebuild replaces the document — the old
   * contentWindow is gone, and a stale reference would answer queries about a page that is no
   * longer on screen. And on every reload: it remounts the frame with the same document, so
   * `doc` is an equal string and would not re-run this — leaving the bridge pinned to the
   * removed frame, deaf to the new one's API calls (a blank page, nobody signed in).
   */
  useEffect(() => {
    if (!doc) return;
    return attachPreview(frameRef.current?.contentWindow ?? null, proxyFetch, onEdgeNav, onNav, onStore);
  }, [doc, reloads, proxyFetch, onEdgeNav, onNav, onStore]);

  return (
    <div className="relative flex h-full flex-col bg-white">
      <PreviewAddressBar
        path={path}
        onGo={previewGo}
        reloading={building}
        onReload={() => {
          refresh();
          setReloads((n) => n + 1);
        }}
      >
        <PreviewIdentityMenu auth={auth} />
        <PreviewVisitorMenu
          siteId={siteId}
          visitor={visitorOfToken(lent['dcms.visitor'])}
          onSignOut={signOutVisitor}
          onReset={signOutVisitor}
        />
      </PreviewAddressBar>
      <div className="relative min-h-0 flex-1">
        {doc ? (
          <iframe
            key={reloads}
            ref={frameRef}
            title={t('ide.preview')}
            srcDoc={doc}
            /* No allow-same-origin: the preview runs tenant- and CDN-authored code, so it must
               execute in an opaque origin — otherwise it shares this admin origin and can read
               the OIDC tokens in localStorage and script the parent. The agent bridge talks to
               it over postMessage (see previewBridge), which works cross-origin, so the sandbox
               costs nothing here. (SEC-10) */
            sandbox="allow-scripts allow-forms allow-modals allow-popups"
            className="h-full w-full border-0 bg-white"
          />
        ) : (
          !error && (
            <div className="flex h-full items-center justify-center text-xs text-muted-foreground">
              {t('ide.previewPending')}
            </div>
          )
        )}
        {error && (
          <div className="absolute inset-x-0 bottom-0 max-h-1/2 overflow-auto border-t border-destructive/40 bg-destructive/10 p-3">
            <div className="mb-1 flex items-center gap-1.5 text-xs font-semibold text-destructive">
              <AlertTriangle className="h-3.5 w-3.5" /> {t('ide.buildError')}
            </div>
            <pre className="whitespace-pre-wrap break-words text-2xs leading-relaxed text-destructive">
              {error}
            </pre>
          </div>
        )}
      </div>
    </div>
  );
}
