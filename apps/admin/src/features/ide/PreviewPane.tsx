import { AlertTriangle, RefreshCw } from 'lucide-react';
import { useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { renewSilently } from '../../auth';
import { adminHeaders } from '../../tenants';
import { attachPreview, type PreviewFetchProxy } from './preview/previewBridge';
import type { PreviewControls } from './preview/usePreview';

// Live client-side preview: the esbuild-wasm worker bundles the project and we
// render the result in a sandboxed iframe. Build errors surface as an overlay.
//
// The build itself is driven by IdePage, not here. Two callers of usePreview would be two
// workers building the same project, and the Problems view needs the same build's messages.

export function PreviewPane({ preview, siteId }: { preview: PreviewControls; siteId: string }) {
  const { t } = useTranslation();
  const { srcdoc, error, building, refresh } = preview;
  const frameRef = useRef<HTMLIFrameElement>(null);

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
      const prefix = `/api/admin/sites/${siteId}/preview/`;
      if (!req.url.startsWith(prefix)) {
        return { status: 403, statusText: 'Forbidden', headers: {}, body: '' };
      }
      const send = async () => {
        const headers: Record<string, string> = { ...(await adminHeaders()) };
        const contentType = req.headers['content-type'] ?? req.headers['Content-Type'];
        if (contentType) headers['Content-Type'] = contentType;
        return fetch(req.url, {
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
      return { status: res.status, statusText: res.statusText, headers, body: await res.text() };
    },
    [siteId],
  );

  /*
   * Connect the running page to the agent's preview tools and the API proxy above.
   *
   * Re-attached on every `srcdoc` change because a rebuild replaces the document — the old
   * contentWindow is gone, and a stale reference would answer queries about a page that is no
   * longer on screen.
   */
  useEffect(() => {
    if (!srcdoc) return;
    return attachPreview(frameRef.current?.contentWindow ?? null, proxyFetch);
  }, [srcdoc, proxyFetch]);

  return (
    <div className="relative flex h-full flex-col bg-white">
      <div className="flex h-8 shrink-0 items-center gap-2 border-b bg-card px-3 text-xs text-muted-foreground">
        <button
          type="button"
          onClick={refresh}
          disabled={building}
          title={t('ide.refresh')}
          aria-label={t('ide.refresh')}
          className="inline-flex items-center rounded p-0.5 transition-colors hover:text-foreground disabled:cursor-default disabled:opacity-70"
        >
          <RefreshCw className={`h-3.5 w-3.5 ${building ? 'animate-spin' : ''}`} />
        </button>
        <span>{building ? t('ide.building') : t('ide.preview')}</span>
      </div>
      <div className="relative min-h-0 flex-1">
        {srcdoc ? (
          <iframe
            ref={frameRef}
            title={t('ide.preview')}
            srcDoc={srcdoc}
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
            <pre className="whitespace-pre-wrap break-words text-[11px] leading-relaxed text-destructive">
              {error}
            </pre>
          </div>
        )}
      </div>
    </div>
  );
}
