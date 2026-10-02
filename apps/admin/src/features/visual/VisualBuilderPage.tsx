import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { APP_JSON, appSchema, builtinRegistry, pageIdFromPath } from '@dcms/site-runtime';
import type { Editor } from 'grapesjs';
import { AlertTriangle, ArrowLeft, Layers, Monitor, Redo2, RefreshCw, Smartphone, Tablet, Undo2, Blocks } from 'lucide-react';
import { useCallback, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Button,
  CenteredSpinner,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  cn,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { BlocksPanel } from '../builder/panels/BlocksPanel';
import { LayersPanel } from '../builder/panels/LayersPanel';
import { Resizer, useDraftSession, useStoredWidth, useVfs } from '../site-source';
import { PropsPanel } from './PropsPanel';
import { VisualCanvas } from './VisualCanvas';
import { VISUAL_DEVICES, type DeviceId } from './canvas/editor';
import { starterFiles } from './starter';

// Widen for TanStack Link typing (sibling routes are registered via a helper).
const sitesPath: string = '/sites';

const DEVICE_ICONS: Record<DeviceId, typeof Monitor> = { desktop: Monitor, tablet: Tablet, mobile: Smartphone };

type SidebarView = 'components' | 'layers';

/**
 * The Mode D builder (ADR 0020): a page is a tree of React components, edited on a GrapesJS
 * canvas that draws them with the same code the published site runs.
 *
 * The source is the same per-site git file map as Modes A and B, through the same draft
 * session; what differs is only what the author edits — `dcms/pages/*.json` through a canvas.
 * Publishing, source control and the agent arrive with the rest of P2.
 */
export function VisualBuilderPage({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const [editor, setEditor] = useState<Editor | null>(null);
  const [sidebar, setSidebar] = useState<SidebarView>('components');
  const [device, setDevice] = useState<DeviceId>('desktop');
  const [pageError, setPageError] = useState<string | null>(null);
  const [canUndo, setCanUndo] = useState(false);
  const [canRedo, setCanRedo] = useState(false);
  const [chosenPage, setChosenPage] = useState<string | null>(null);

  const [sidebarWidth, setSidebarWidth, resetSidebarWidth] = useStoredWidth('dcms.visual.sidebarWidth', 260, 200, 480);
  const [inspectorWidth, setInspectorWidth, resetInspectorWidth] = useStoredWidth('dcms.visual.inspectorWidth', 300, 220, 520);

  const site = useQuery({
    queryKey: ['site', siteId],
    queryFn: () => api.get<{ name: string }>(`/admin/sites/${siteId}`),
  });
  const session = useDraftSession({ siteId, seed: () => starterFiles(site.data?.name) });

  const dirty = useVfs((s) => s.dirty);
  const conflict = useVfs((s) => s.conflict);
  const branch = useVfs((s) => s.branch);
  const files = useVfs((s) => s.files);

  const pages = useMemo(
    () =>
      Object.keys(files)
        .map(pageIdFromPath)
        .filter((id): id is string => id !== null)
        .sort(),
    [files],
  );
  const homePage = useMemo(() => {
    try {
      const app = appSchema.safeParse(JSON.parse(files[APP_JSON] ?? ''));
      return app.success ? app.data.routes.find((r) => r.path === '/')?.page : undefined;
    } catch {
      return undefined;
    }
  }, [files]);
  const pageId =
    (chosenPage && pages.includes(chosenPage) ? chosenPage : null) ??
    (homePage && pages.includes(homePage) ? homePage : null) ??
    pages[0] ??
    null;

  const onEditorReady = useCallback((instance: Editor) => {
    setEditor(instance);
    const sync = () => {
      setCanUndo(instance.UndoManager.hasUndo());
      setCanRedo(instance.UndoManager.hasRedo());
    };
    instance.on('update undo redo', sync);
  }, []);

  const chooseDevice = (id: DeviceId) => {
    setDevice(id);
    editor?.setDevice(VISUAL_DEVICES.find((d) => d.id === id)!.name);
  };

  if (site.isLoading || !session.ready) return <CenteredSpinner label={t('common.loading')} />;

  return (
    <div className="flex h-[calc(100vh-3.5rem)] flex-col">
      <div className="flex h-12 shrink-0 items-center gap-2 border-b bg-card px-3">
        <Link to={sitesPath}>
          <Button size="icon" variant="ghost" aria-label={t('sites.title')}>
            <ArrowLeft className="h-4 w-4" />
          </Button>
        </Link>
        <span className="font-medium">{site.data?.name}</span>
        <span className="text-xs text-muted-foreground">{t('sites.modeReactBuilder')}</span>

        {pages.length > 0 && pageId && (
          <Select value={pageId} onValueChange={setChosenPage}>
            <SelectTrigger className="ml-2 h-8 w-48" aria-label={t('visual.page')}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {pages.map((id) => (
                <SelectItem key={id} value={id}>
                  {id}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}

        <div className="mx-2 flex items-center gap-1 rounded-md border bg-muted p-0.5" role="group" aria-label={t('visual.device')}>
          {VISUAL_DEVICES.map(({ id }) => {
            const Icon = DEVICE_ICONS[id];
            return (
              <button
                key={id}
                type="button"
                title={t(`visual.devices.${id}`)}
                aria-pressed={device === id}
                onClick={() => chooseDevice(id)}
                className={cn(
                  'flex h-7 w-8 items-center justify-center rounded',
                  device === id ? 'bg-background shadow-sm' : 'text-muted-foreground',
                )}
              >
                <Icon className="h-4 w-4" />
              </button>
            );
          })}
        </div>

        <Button size="icon" variant="ghost" disabled={!canUndo} onClick={() => editor?.UndoManager.undo()} title={t('actions.undo')}>
          <Undo2 className="h-4 w-4" />
        </Button>
        <Button size="icon" variant="ghost" disabled={!canRedo} onClick={() => editor?.UndoManager.redo()} title={t('actions.redo')}>
          <Redo2 className="h-4 w-4" />
        </Button>

        <div className="flex-1" />
        <span className="text-xs text-muted-foreground">
          {branch} · {dirty ? t('common.saving') : session.status}
        </span>
      </div>

      {conflict && (
        <div className="flex shrink-0 items-center gap-2 border-b bg-destructive/10 px-3 py-2 text-sm text-destructive">
          <AlertTriangle className="h-4 w-4 shrink-0" />
          <span className="min-w-0 flex-1">{t('ide.conflictWarning', { files: conflict.join(', ') })}</span>
          <Button size="sm" variant="outline" onClick={() => session.openBranch(branch)}>
            <RefreshCw className="h-4 w-4" /> {t('ide.reloadLatest')}
          </Button>
        </div>
      )}
      {pageError && (
        <div className="flex shrink-0 items-center gap-2 border-b bg-amber-500/10 px-3 py-2 text-sm text-amber-700 dark:text-amber-400">
          <AlertTriangle className="h-4 w-4 shrink-0" />
          <span className="min-w-0 flex-1">{t('visual.pageError', { error: pageError })}</span>
        </div>
      )}

      <div className="relative flex min-h-0 flex-1">
        <div style={{ width: sidebarWidth }} className="flex min-w-0 shrink-0 flex-col border-r bg-card">
          <div className="flex shrink-0 gap-1 border-b p-1" role="tablist">
            {(
              [
                ['components', Blocks, 'visual.components'],
                ['layers', Layers, 'builder.layers'],
              ] as const
            ).map(([id, Icon, label]) => (
              <button
                key={id}
                type="button"
                role="tab"
                aria-selected={sidebar === id}
                onClick={() => setSidebar(id)}
                className={cn(
                  'flex flex-1 items-center justify-center gap-1.5 rounded px-2 py-1.5 text-xs',
                  sidebar === id ? 'bg-muted font-medium' : 'text-muted-foreground hover:bg-muted/60',
                )}
              >
                <Icon className="h-3.5 w-3.5" /> {t(label)}
              </button>
            ))}
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto">
            {/* Both stay mounted: the block palette's payload arrives once, at editor creation. */}
            <div className={cn(sidebar !== 'components' && 'hidden')}>
              <BlocksPanel editor={editor} />
            </div>
            <div className={cn(sidebar !== 'layers' && 'hidden')}>
              <LayersPanel editor={editor} />
            </div>
          </div>
        </div>
        <Resizer onDelta={(dx) => setSidebarWidth(sidebarWidth + dx)} onReset={resetSidebarWidth} />

        <div className="min-w-0 flex-1 bg-muted/40">
          {pageId ? (
            <VisualCanvas
              pageId={pageId}
              registry={builtinRegistry}
              onReady={onEditorReady}
              onTeardown={() => setEditor(null)}
              onPageError={setPageError}
            />
          ) : (
            <div className="flex h-full items-center justify-center p-6 text-sm text-muted-foreground">
              {t('visual.noPages')}
            </div>
          )}
        </div>

        <Resizer onDelta={(dx) => setInspectorWidth(inspectorWidth - dx)} onReset={resetInspectorWidth} />
        <div style={{ width: inspectorWidth }} className="min-w-0 shrink-0 border-l bg-card">
          <PropsPanel editor={editor} registry={builtinRegistry} />
        </div>
      </div>
    </div>
  );
}
