import { AlertTriangle, RefreshCw } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { usePreview } from './preview/usePreview';

// Live client-side preview: the esbuild-wasm worker bundles the project and we
// render the result in a sandboxed iframe. Build errors surface as an overlay.

export function PreviewPane({
  enabled,
  siteId,
  refreshKey,
}: {
  enabled: boolean;
  siteId: string;
  refreshKey: number;
}) {
  const { t } = useTranslation();
  const { srcdoc, error, building, refresh } = usePreview(enabled, siteId, refreshKey);

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
