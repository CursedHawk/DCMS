import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import {
  APP_JSON,
  BUILTIN_COMPONENTS,
  checkVisualSite,
  componentDependencies,
  componentFileOf,
  listPath,
  parseItemList,
  readComponentDocs,
  siteRegistry,
  tenantType,
} from '@dcms/site-runtime';
import type { Editor } from 'grapesjs';
import {
  AlertCircle,
  AlertTriangle,
  ArrowLeft,
  Blocks,
  Code2,
  Columns2,
  Eye,
  FileText,
  GitBranch,
  Layers,
  Monitor,
  MousePointer2,
  Palette,
  Puzzle,
  Redo2,
  RefreshCw,
  Rocket,
  Smartphone,
  Sparkles,
  Tablet,
  Undo2,
  X,
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button, CenteredSpinner, cn } from '@dcms/ui';
import { ApiError, api } from '../../lib/api';
import { useAuth } from '../../useAuth';
import { MediaBridge } from '../builder/panels/MediaBridge';
import { BlocksPanel } from '../builder/panels/BlocksPanel';
import { LayersPanel } from '../builder/panels/LayersPanel';
import { useGeneratedApi } from '../ide/generated/useGeneratedApi';
import {
  DeploymentsView,
  MergeDialog,
  RELEASE_BRANCH,
  Resizer,
  SourceControlView,
  gitApi,
  ideApi,
  useDraftSession,
  useSiteLiveUpdates,
  useStoredWidth,
  useVfs,
} from '../site-source';
import { PagesPanel } from './PagesPanel';
import { readComponent, readPage } from './documents';
import { ComponentsPanel } from './ComponentsPanel';
import { ProblemsPanel } from './ProblemsPanel';
import { PropsPanel } from './PropsPanel';
import { ThemePanel } from './ThemePanel';
import { VisualCanvas } from './VisualCanvas';
import { VisualCodeView } from './VisualCodeView';
import { VisualPreview } from './VisualPreview';
import { VISUAL_DEVICES, type DeviceId } from './canvas/editor';
import { starterFiles } from './starter';
import { useVisual, type VisualView } from './store';
import { useSiteRuntime } from './useSiteRuntime';
import { AgentPanel } from '../ide/agent/AgentPanel';
import { VISUAL_TOOLS, validateVisualSite } from './agent/visualTools';
import { previewClient, useContentCatalog } from './data';

// Widen for TanStack Link typing (sibling routes are registered via a helper).
const sitesPath: string = '/sites';

const DEVICE_ICONS: Record<DeviceId, typeof Monitor> = { desktop: Monitor, tablet: Tablet, mobile: Smartphone };
const VIEWS: { id: VisualView; icon: typeof Code2; labelKey: string }[] = [
  { id: 'design', icon: MousePointer2, labelKey: 'builder.viewDesign' },
  { id: 'split', icon: Columns2, labelKey: 'builder.viewSplit' },
  { id: 'code', icon: Code2, labelKey: 'builder.viewCode' },
  { id: 'preview', icon: Eye, labelKey: 'visual.preview' },
];

type SidebarView = 'components' | 'mine' | 'layers' | 'pages' | 'theme' | 'scm' | 'deploy' | 'problems' | 'agent';

/**
 * The Mode D builder (ADR 0020): a page is a tree of React components, edited on a GrapesJS
 * canvas that draws them with the same code the published site runs.
 *
 * The source is the same per-site git file map as Modes A and B, through the same draft session,
 * source control, deployments and "publish = ship this branch to release" flow; what differs is
 * what the author edits — `dcms/**.json` through a canvas — and what keeps the repository a
 * buildable Vite app: the scaffold on the first open, and the runtime and API layers refreshed
 * on every open after.
 */
export function VisualBuilderPage({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [editor, setEditor] = useState<Editor | null>(null);
  const [sidebar, setSidebar] = useState<SidebarView>('components');
  const [pageError, setPageError] = useState<string | null>(null);
  const [canUndo, setCanUndo] = useState(false);
  const [canRedo, setCanRedo] = useState(false);
  const [publishMerge, setPublishMerge] = useState(false);
  const [branchMoved, setBranchMoved] = useState(false);
  const [draftMovedPaths, setDraftMovedPaths] = useState<string[] | null>(null);

  const [sidebarWidth, setSidebarWidth, resetSidebarWidth] = useStoredWidth('dcms.visual.sidebarWidth', 300, 220, 520);
  const [inspectorWidth, setInspectorWidth, resetInspectorWidth] = useStoredWidth('dcms.visual.inspectorWidth', 300, 220, 520);
  const [codeWidth, setCodeWidth, resetCodeWidth] = useStoredWidth('dcms.visual.codeWidth', 560, 280, 1400);

  const target = useVisual((s) => s.target);
  const device = useVisual((s) => s.device);
  const view = useVisual((s) => s.view);
  const app = useVisual((s) => s.app);

  const site = useQuery({
    queryKey: ['site', siteId],
    queryFn: () => api.get<{ name: string }>(`/admin/sites/${siteId}`),
  });

  // A brand-new site is the visual scaffold (a Vite app with the runtime and this tenant's API
  // client, from the server) plus the starter documents. Seeded once, on the first empty load.
  const scaffold = useMutation({
    mutationFn: () => ideApi.scaffold(siteId, 'visual'),
    onSuccess: ({ files }) => useVfs.getState().seedStarter({ ...files, ...starterFiles(site.data?.name) }),
    onError: () => toast.error(t('visual.scaffoldFailed')),
  });
  const session = useDraftSession({
    siteId,
    seed: () => null,
    onLoaded: (_branch, isEmpty) => {
      if (isEmpty) scaffold.mutate();
    },
  });

  const dirty = useVfs((s) => s.dirty);
  const conflict = useVfs((s) => s.conflict);
  const branch = useVfs((s) => s.branch);
  const files = useVfs((s) => s.files);
  const draftVersion = useVfs((s) => s.version);

  const settled = session.ready && !session.switching && !conflict && !scaffold.isPending && files[APP_JSON] !== undefined;
  useGeneratedApi({ siteId, settled });
  useSiteRuntime({ settled });

  const { user } = useAuth();
  useSiteLiveUpdates({
    siteId,
    branch,
    myUserId: user?.profile.sub,
    onCommit: () => setBranchMoved(true),
    draftVersion,
    onDraftChanged: (d) => setDraftMovedPaths(d.paths),
  });
  useEffect(() => setBranchMoved(false), [branch]);

  // The app document, for menus on the canvas and the panels that edit it.
  useEffect(() => useVisual.getState().syncApp(files[APP_JSON]), [files]);

  // Open the home page first, and follow it if the page being edited is deleted.
  useEffect(() => {
    if (!app) return;
    const current = useVisual.getState().target;
    const pageExists = (id: string) => files[`dcms/pages/${id}.json`] !== undefined;
    if (current?.kind === 'shell' || current?.kind === 'component' || (current?.kind === 'page' && pageExists(current.id))) return;
    const home = app.routes.find((r) => r.path === '/')?.page ?? app.routes[0]?.page;
    if (home && pageExists(home)) useVisual.getState().setTarget({ kind: 'page', id: home });
  }, [app, files]);

  // The site's own components, keyed by their files' text so an unrelated edit does not rebuild
  // the registry — every rebuild re-registers the canvas types and reloads the canvas.
  const componentsKey = useMemo(
    () =>
      Object.entries(files)
        .filter(([p]) => componentFileOf(p))
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([p, text]) => `${p}\n${text}`)
        .join('\u0000'),
    [files],
  );
  const componentDocs = useMemo(() => {
    const byPath = new Map<string, unknown>();
    for (const [path, text] of Object.entries(useVfs.getState().files)) {
      if (!componentFileOf(path)) continue;
      try {
        byPath.set(path, JSON.parse(text));
      } catch {
        // Reported by the problems list.
      }
    }
    return readComponentDocs(byPath).docs;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [componentsKey]);
  const registry = useMemo(() => siteRegistry(BUILTIN_COMPONENTS, componentDocs).registry, [componentDocs]);
  useEffect(() => useVisual.getState().setRegistry(registry), [registry]);

  // While a component is open, neither it nor anything that already contains it may be dropped
  // into it: a component inside itself would render forever.
  useEffect(() => {
    if (target?.kind !== 'component') {
      useVisual.getState().setBlockedTypes(new Set());
      return;
    }
    const contains = new Map(componentDocs.map((d) => [tenantType(d.name), componentDependencies(d)]));
    const blocked = new Set([tenantType(target.name)]);
    for (let grew = true; grew; ) {
      grew = false;
      for (const [type, deps] of contains) {
        if (!blocked.has(type) && [...deps].some((d) => blocked.has(d))) {
          blocked.add(type);
          grew = true;
        }
      }
    }
    useVisual.getState().setBlockedTypes(blocked);
  }, [target, componentDocs]);

  // The canvas and preview read real published content through the admin's preview proxy.
  const dataClient = useMemo(() => previewClient(siteId), [siteId]);
  useEffect(() => useVisual.getState().setDataClient(dataClient), [dataClient]);

  // A detail page is designed with a real item in it: its source's first.
  const pageData = target?.kind === 'page' ? readPage(target.id)?.data : undefined;
  const pageSourceKey = pageData ? listPath(pageData.source, { limit: 1 }) : null;
  useEffect(() => {
    useVisual.getState().setPageItem(null);
    if (!pageSourceKey) return;
    let live = true;
    dataClient.get(pageSourceKey).then(
      (json) => live && useVisual.getState().setPageItem(parseItemList(json).items[0] ?? null),
      () => live && useVisual.getState().setPageItem(null),
    );
    return () => {
      live = false;
    };
  }, [pageSourceKey, dataClient]);
  const pageItem = useVisual((s) => s.pageItem);

  // With the tenant's content types known, the validator also checks sources and bound fields.
  const catalog = useContentCatalog();
  const contentSchema = catalog.isLoading ? undefined : catalog.schema;
  const problems = useMemo(() => checkVisualSite(files, registry, contentSchema), [files, registry, contentSchema]);
  // The agent's checks read it from the store: it must see the problems this list shows.
  useEffect(() => useVisual.getState().setContentSchema(contentSchema), [contentSchema]);
  const errors = problems.filter((p) => p.severity === 'error').length;

  const onEditorReady = useCallback((instance: Editor) => {
    setEditor(instance);
    const sync = () => {
      setCanUndo(instance.UndoManager.hasUndo());
      setCanRedo(instance.UndoManager.hasRedo());
    };
    instance.on('update undo redo', sync);
    instance.setDevice(VISUAL_DEVICES.find((d) => d.id === useVisual.getState().device)!.name);
  }, []);

  const chooseDevice = (id: DeviceId) => {
    useVisual.getState().setDevice(id);
    editor?.setDevice(VISUAL_DEVICES.find((d) => d.id === id)!.name);
  };

  const publish = useMutation({
    mutationFn: async () => {
      await session.flush();
      await gitApi.commit(siteId, { branch, message: `Publish ${branch}` });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['git-changes', siteId] });
      queryClient.invalidateQueries({ queryKey: ['git-history', siteId] });
      if (branch === RELEASE_BRANCH) {
        toast.success(t('editor.publishQueued'));
        setSidebar('deploy');
      } else {
        setPublishMerge(true);
      }
    },
    onError: (e) => {
      if (e instanceof ApiError && e.status === 409) {
        toast.error(t('ide.git.resolveInScm'));
        setSidebar('scm');
      } else {
        toast.error(t('errors.generic'));
      }
    },
  });

  const startPublish = () => {
    // The same report the AI gets: a site the validator calls broken is not shipped.
    if (errors > 0) {
      toast.error(t('visual.problems.blockPublish', { count: errors }));
      setSidebar('problems');
      return;
    }
    publish.mutate();
  };

  const changes = useQuery({
    queryKey: ['git-changes', siteId, branch],
    queryFn: () => gitApi.changes(siteId, branch),
    enabled: session.ready,
  });

  if (site.isLoading || !session.ready || scaffold.isPending) return <CenteredSpinner label={t('common.loading')} />;

  const showCanvas = view === 'design' || view === 'split';
  const showCode = view === 'split' || view === 'code';
  const pageRoute = target?.kind === 'page' ? app?.routes.find((r) => r.page === target.id) : undefined;
  const previewPath = !pageRoute
    ? '/'
    : !pageRoute.path.includes(':')
      ? pageRoute.path
      : pageItem
        ? pageRoute.path.replace(/:[A-Za-z][A-Za-z0-9]*/, encodeURIComponent(pageItem.slug))
        : '/';
  const targetLabel =
    target?.kind === 'shell'
      ? t('visual.pages.shell')
      : target?.kind === 'component'
        ? t('visual.mine.editing', { label: readComponent(target.name, target.version)?.label ?? target.name, version: target.version })
        : target
          ? (readPage(target.id)?.title ?? target.id)
          : '';

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
        {targetLabel && (
          <button
            type="button"
            onClick={() => setSidebar('pages')}
            className="ml-2 max-w-48 truncate rounded-md border px-2 py-1 text-xs"
            title={t('builder.pages')}
          >
            {targetLabel}
          </button>
        )}

        <Segmented label={t('visual.view')}>
          {VIEWS.map(({ id, icon: Icon, labelKey }) => (
            <SegmentButton key={id} active={view === id} title={t(labelKey)} onClick={() => useVisual.getState().setView(id)}>
              <Icon className="h-4 w-4" />
            </SegmentButton>
          ))}
        </Segmented>

        <Segmented label={t('visual.device')}>
          {VISUAL_DEVICES.map(({ id }) => {
            const Icon = DEVICE_ICONS[id];
            return (
              <SegmentButton key={id} active={device === id} title={t(`visual.devices.${id}`)} onClick={() => chooseDevice(id)}>
                <Icon className="h-4 w-4" />
              </SegmentButton>
            );
          })}
        </Segmented>

        <Button size="icon" variant="ghost" disabled={!canUndo} onClick={() => editor?.UndoManager.undo()} title={t('actions.undo')}>
          <Undo2 className="h-4 w-4" />
        </Button>
        <Button size="icon" variant="ghost" disabled={!canRedo} onClick={() => editor?.UndoManager.redo()} title={t('actions.redo')}>
          <Redo2 className="h-4 w-4" />
        </Button>

        <div className="flex-1" />
        {problems.length > 0 && (
          <button
            type="button"
            onClick={() => setSidebar('problems')}
            className={cn('flex items-center gap-1 rounded px-2 py-1 text-xs', errors ? 'text-destructive' : 'text-amber-600')}
          >
            {errors ? <AlertCircle className="h-3.5 w-3.5" /> : <AlertTriangle className="h-3.5 w-3.5" />}
            {t('visual.problems.count', { count: problems.length })}
          </button>
        )}
        <span className="text-xs text-muted-foreground">
          {branch} · {dirty ? t('common.saving') : session.status}
        </span>
        <Button onClick={startPublish} disabled={publish.isPending || !!conflict || session.switching}>
          <Rocket className="h-4 w-4" /> {t('ide.git.publishToRelease')}
        </Button>
      </div>

      <MediaBridge editor={editor} />
      <MergeDialog
        siteId={siteId}
        head={branch}
        open={publishMerge}
        onOpenChange={setPublishMerge}
        onMerged={() => {
          queryClient.invalidateQueries({ queryKey: ['git-history', siteId] });
          queryClient.invalidateQueries({ queryKey: ['git-changes', siteId] });
          queryClient.invalidateQueries({ queryKey: ['site-builds', siteId] });
          setSidebar('deploy');
        }}
      />

      {draftMovedPaths && !conflict && (
        <Banner tone="warning" icon={<AlertTriangle className="h-4 w-4 shrink-0" />} message={t('ide.draftMovedWarning', { files: draftMovedPaths.slice(0, 3).join(', ') })}>
          <Button size="sm" variant="outline" onClick={() => session.openBranch(branch)} disabled={session.switching}>
            <RefreshCw className="h-4 w-4" /> {t('ide.reloadLatest')}
          </Button>
          <button type="button" onClick={() => setDraftMovedPaths(null)} aria-label={t('actions.dismiss')} className="rounded p-1">
            <X className="h-4 w-4" />
          </button>
        </Banner>
      )}
      {(conflict || branchMoved) && (
        <Banner
          tone="destructive"
          icon={<AlertTriangle className="h-4 w-4 shrink-0" />}
          message={conflict ? t('ide.conflictWarning', { files: conflict.join(', ') }) : t('ide.branchMovedWarning', { branch })}
        >
          <Button size="sm" variant="outline" onClick={() => session.openBranch(branch)}>
            <RefreshCw className="h-4 w-4" /> {t('ide.reloadLatest')}
          </Button>
        </Banner>
      )}
      {pageError && view !== 'code' && (
        <Banner tone="warning" icon={<AlertTriangle className="h-4 w-4 shrink-0" />} message={t('visual.pageError', { error: pageError })}>
          <Button size="sm" variant="outline" onClick={() => useVisual.getState().setView('split')}>
            <Code2 className="h-4 w-4" /> {t('builder.openCode')}
          </Button>
        </Banner>
      )}

      <div className="relative flex min-h-0 flex-1">
        <div style={{ width: sidebarWidth }} className="flex min-w-0 shrink-0 border-r bg-card">
          <div className="flex w-11 shrink-0 flex-col items-center gap-1 border-r py-2" role="tablist" aria-orientation="vertical">
            <Rail active={sidebar === 'components'} label={t('visual.components')} onClick={() => setSidebar('components')}><Blocks className="h-5 w-5" /></Rail>
            <Rail active={sidebar === 'mine'} label={t('visual.mine.title')} onClick={() => setSidebar('mine')}><Puzzle className="h-5 w-5" /></Rail>
            <Rail active={sidebar === 'layers'} label={t('builder.layers')} onClick={() => setSidebar('layers')}><Layers className="h-5 w-5" /></Rail>
            <Rail active={sidebar === 'pages'} label={t('builder.pages')} onClick={() => setSidebar('pages')}><FileText className="h-5 w-5" /></Rail>
            <Rail active={sidebar === 'theme'} label={t('visual.theme.title')} onClick={() => setSidebar('theme')}><Palette className="h-5 w-5" /></Rail>
            <Rail active={sidebar === 'scm'} label={t('ide.git.title')} onClick={() => setSidebar('scm')} badge={changes.data?.length ?? 0}><GitBranch className="h-5 w-5" /></Rail>
            <Rail active={sidebar === 'deploy'} label={t('ide.deploy.title')} onClick={() => setSidebar('deploy')}><Rocket className="h-5 w-5" /></Rail>
            <Rail active={sidebar === 'problems'} label={t('visual.problems.title')} onClick={() => setSidebar('problems')} badge={errors}><AlertCircle className="h-5 w-5" /></Rail>
            <Rail active={sidebar === 'agent'} label={t('ide.agent.title')} onClick={() => setSidebar('agent')}><Sparkles className="h-5 w-5" /></Rail>
          </div>
          <div className="min-w-0 flex-1 overflow-hidden">
            {/* The palette stays mounted: its payload arrives once, at editor creation. */}
            <div className={cn('h-full overflow-y-auto', sidebar !== 'components' && 'hidden')}>
              <BlocksPanel editor={editor} />
            </div>
            {sidebar === 'mine' && <ComponentsPanel editor={editor} />}
            {sidebar === 'layers' && <LayersPanel editor={editor} />}
            {sidebar === 'pages' && <PagesPanel />}
            {sidebar === 'theme' && <ThemePanel />}
            {sidebar === 'scm' && (
              <SourceControlView
                siteId={siteId}
                branch={branch}
                onSwitchBranch={(b) => session.openBranch(b)}
                onReload={() => session.openBranch(branch)}
                onRestored={(restored, version, hashes) => useVfs.getState().load(restored, version, hashes)}
              />
            )}
            {sidebar === 'deploy' && <DeploymentsView siteId={siteId} />}
            {sidebar === 'problems' && <ProblemsPanel problems={problems} editor={editor} />}
            {sidebar === 'agent' && (
              <AgentPanel siteId={siteId} siteName={site.data?.name} kind="visual" tools={VISUAL_TOOLS} validate={validateVisualSite} />
            )}
          </div>
        </div>
        <Resizer onDelta={(dx) => setSidebarWidth(sidebarWidth + dx)} onReset={resetSidebarWidth} />

        <div className="flex min-w-0 flex-1">
          {/* The canvas stays mounted when hidden: unmounting destroys GrapesJS, and with it the
              undo history and every panel's handle on the editor. */}
          <div className={cn('min-w-0 flex-1 bg-muted/40', !showCanvas && 'hidden')}>
            {target ? (
              <VisualCanvas
                target={target}
                registry={registry}
                onReady={onEditorReady}
                onTeardown={() => setEditor(null)}
                onPageError={setPageError}
              />
            ) : (
              <div className="flex h-full items-center justify-center p-6 text-sm text-muted-foreground">{t('visual.noPages')}</div>
            )}
          </div>
          {view === 'preview' && (
            <div className="min-w-0 flex-1">
              <VisualPreview initialPath={previewPath} width={VISUAL_DEVICES.find((d) => d.id === device)?.width} />
            </div>
          )}
          {showCode && showCanvas && (
            <Resizer onDelta={(dx) => setCodeWidth(codeWidth - dx)} onReset={resetCodeWidth} ariaLabel={t('builder.resizeCode')} />
          )}
          {showCode && (
            <div style={showCanvas ? { width: codeWidth } : undefined} className={cn('min-w-0 border-l', showCanvas ? 'max-w-[75%] shrink-0' : 'flex-1')}>
              <VisualCodeView activePage={target?.kind === 'page' ? target.id : null} />
            </div>
          )}
        </div>

        {view !== 'preview' && (
          <>
            <Resizer onDelta={(dx) => setInspectorWidth(inspectorWidth - dx)} onReset={resetInspectorWidth} />
            <div style={{ width: inspectorWidth }} className="min-w-0 shrink-0 border-l bg-card">
              <PropsPanel editor={editor} registry={registry} />
            </div>
          </>
        )}
      </div>
    </div>
  );
}

function Segmented({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="mx-1 flex items-center gap-1 rounded-md border bg-muted p-0.5" role="group" aria-label={label}>
      {children}
    </div>
  );
}

function SegmentButton({ active, title, onClick, children }: { active: boolean; title: string; onClick: () => void; children: ReactNode }) {
  return (
    <button
      type="button"
      title={title}
      aria-pressed={active}
      onClick={onClick}
      className={cn('flex h-7 w-8 items-center justify-center rounded', active ? 'bg-background shadow-sm' : 'text-muted-foreground')}
    >
      {children}
    </button>
  );
}

function Rail({ active, label, onClick, badge, children }: { active: boolean; label: string; onClick: () => void; badge?: number; children: ReactNode }) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      aria-label={label}
      title={label}
      onClick={onClick}
      className={cn(
        'relative flex h-9 w-9 items-center justify-center rounded-md',
        active ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:bg-accent/50',
      )}
    >
      {children}
      {badge ? (
        <span className="absolute -right-0.5 -top-0.5 min-w-4 rounded-full bg-primary px-1 text-[10px] leading-4 text-primary-foreground">
          {badge > 99 ? '99+' : badge}
        </span>
      ) : null}
    </button>
  );
}

function Banner({ tone, icon, message, children }: { tone: 'destructive' | 'warning'; icon: ReactNode; message: string; children: ReactNode }) {
  return (
    <div
      className={cn(
        'flex shrink-0 items-center gap-2 border-b px-3 py-2 text-sm',
        tone === 'destructive' ? 'bg-destructive/10 text-destructive' : 'bg-amber-500/10 text-amber-700 dark:text-amber-400',
      )}
    >
      {icon}
      <span className="min-w-0 flex-1">{message}</span>
      {children}
    </div>
  );
}
