import { AlertTriangle, RefreshCw } from 'lucide-react';
import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { attachPreview } from './preview/previewBridge';
import type { PreviewControls } from './preview/usePreview';

// Live client-side preview: the esbuild-wasm worker bundles the project and we
// render the result in a sandboxed iframe. Build errors surface as an overlay.
//
// The build itself is driven by IdePage, not here. Two callers of usePreview would be two
// workers building the same project, and the Problems view needs the same build's messages.

export function PreviewPane({ preview }: { preview: PreviewControls }) {
  const { t } = useTranslation();
  const { srcdoc, error, building, refresh } = preview;
  const frameRef = useRef<HTMLIFrameElement>(null);

  /*
   * Connect the running page to the agent's preview tools.
   *
   * Re-attached on every `srcdoc` change because a rebuild replaces the document — the old
   * contentWindow is gone, and a stale reference would answer queries about a page that is no
   * longer on screen.
   */
  useEffect(() => {
    if (!srcdoc) return;
    return attachPreview(frameRef.current?.contentWindow ?? null);
  }, [srcdoc]);

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
            sandbox="allow-scripts allow-same-origin allow-forms allow-modals allow-popups"
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
