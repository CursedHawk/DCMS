import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import type { Editor } from 'grapesjs';
import {
  AlertTriangle,
  ArrowLeft,
  Code2,
  Columns2,
  LayoutTemplate,
  MousePointer2,
  Puzzle,
  RefreshCw,
  Redo2,
  Rocket,
  Sparkles,
  Undo2,
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button, CenteredSpinner, cn } from '@dcms/ui';
import { ApiError, api } from '../../lib/api';
import { useAuth } from '../../useAuth';
import {
  MergeDialog,
  RELEASE_BRANCH,
  Resizer,
  expectBuild,
  gitApi,
  useDraftSession,
  useSiteLiveUpdates,
  useStoredWidth,
  useVfs,
} from '../site-source';
import { coreSpecsFor, specsForComponents } from '@dcms/gjs-blocks';
import { homePage } from '@dcms/gjs-schema';
import { usePluginComponentSpecs } from './plugins/specs';
import { BuilderCanvas } from './BuilderCanvas';
import { BuilderSidebar, type SidebarView } from './BuilderSidebar';
import { Inspector } from './Inspector';
import { CodeView } from './code/CodeView';
import { AiPanel } from './ai/AiPanel';
import { AssetsBridge } from './panels/AssetsBridge';
import { MediaBridge } from './panels/MediaBridge';
import { RegionChrome } from './panels/RegionChrome';
import { TemplateHints } from './panels/TemplateHints';
import { PreviewBridge } from './plugins/PreviewBridge';
import { starterFiles } from './starter';
import { useBuilder, type ViewMode } from './store';

// Widen for TanStack Link typing (sibling routes are registered via a helper).
const sitesPath: string = '/sites';

const VIEWS: { id: ViewMode; icon: typeof Code2; labelKey: string }[] = [
  { id: 'design', icon: MousePointer2, labelKey: 'builder.viewDesign' },
  { id: 'split', icon: Columns2, labelKey: 'builder.viewSplit' },
  { id: 'code', icon: Code2, labelKey: 'builder.viewCode' },
];

/**
 * The Mode A visual builder.
 *
 * Its source is a file map in a per-site git repo, exactly like the Mode B IDE:
 * the same per-branch working draft, the same granular autosave and conflict
 * handling, the same Source Control and Deployments panels, and the same
 * "publish = ship this branch to `release`" flow. What differs is only what sits
 * between the author and those files — a canvas and a code view over
 * `site.json`, `pages/*.html` and `styles/*.css`.
 */
export function BuilderPage({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  // State, not a ref: the panels are rendered from it, so they must re-render
  // when the canvas finishes creating the editor.
  const [editor, setEditor] = useState<Editor | null>(null);

  const rev = useVfs((s) => s.rev);
  const generation = useVfs((s) => s.generation);
  const dirty = useVfs((s) => s.dirty);
  const conflict = useVfs((s) => s.conflict);
  const branch = useVfs((s) => s.branch);
  const activeDiff = useVfs((s) => s.activeDiff);

  const project = useBuilder((s) => s.project);
  const projectError = useBuilder((s) => s.error);
  const activeKind = useBuilder((s) => s.activeKind);
  const activeSlug = useBuilder((s) => s.activeSlug);
  const view = useBuilder((s) => s.view);
  const setView = useBuilder((s) => s.setView);

  const [sidebarView, setSidebarView] = useState<SidebarView>('blocks');
  const [aiOpen, setAiOpen] = useState(false);
  const [publishMerge, setPublishMerge] = useState(false);
  const [canUndo, setCanUndo] = useState(false);
  const [canRedo, setCanRedo] = useState(false);

  const [sidebarWidth, setSidebarWidth, resetSidebarWidth] = useStoredWidth(
    'dcms.builder.sidebarWidth',
    280,
    200,
    520,
  );
  const [inspectorWidth, setInspectorWidth, resetInspectorWidth] = useStoredWidth(
    'dcms.builder.inspectorWidth',
    300,
    220,
    560,
  );
  // The code pane's width in split view. Stored in px rather than as a fraction
  // so it survives a window resize the way the other two panes do.
  const [codeWidth, setCodeWidth, resetCodeWidth] = useStoredWidth(
    'dcms.builder.codeWidth',
    560,
    280,
    1400,
  );

  const site = useQuery({
    queryKey: ['site', siteId],
    queryFn: () => api.get<{ name: string }>(`/admin/sites/${siteId}`),
  });

  // Live deployments and commits, shared with the Mode B IDE — both modes publish through the
  // same release branch and render the same Deployments panel.
  const { user } = useAuth();
  const [branchMoved, setBranchMoved] = useState(false);
  useSiteLiveUpdates({
    siteId,
    branch,
    myUserId: user?.profile.sub,
    onCommit: () => setBranchMoved(true),
  });
  useEffect(() => setBranchMoved(false), [branch]);

  const session = useDraftSession({ siteId, seed: () => starterFiles(site.data?.name) });

  // Blocks that need a plugin (forms, blog lists) are only offered when that
  // plugin is actually enabled — a form that silently posts nowhere is worse
  // than a form that was never in the palette. Alongside those, the tenant's own
  // plugin instances generate a component each, so a newly installed plugin
  // appears in the palette with no deploy.
  const plugins = usePluginComponentSpecs();

  // The tenant's own components, from `blocks/*.json` in this site's repo. They
  // become ordinary specs so the palette, the inspector and the code view treat
  // one the same as a built-in — which is what stops the component builder from
  // producing second-class blocks.
  //
  // Keyed by *content*: the project is re-read from the working draft on every
  // autosave, so the array itself is new every time and recomputing on its
  // identity would re-expand every snippet, re-teach Monaco its component data
  // and re-register the canvas types several times a second while typing.
  const projectComponents = useBuilder((s) => s.project?.components);
  const componentsKey = useMemo(() => JSON.stringify(projectComponents ?? []), [projectComponents]);
  const customSpecs = useMemo(
    // Read fresh rather than closing over the array, which is deliberately not a
    // dependency here.
    () => specsForComponents(useBuilder.getState().project?.components ?? [], document),
    [componentsKey],
  );

  // The same spec list drives the palette, the canvas and the code view's
  // completions and diagnostics, so the three cannot describe different
  // components.
  const specs = useMemo(
    () => [...coreSpecsFor(plugins.enabledPluginIds), ...plugins.specs, ...customSpecs],
    [plugins.enabledPluginIds, plugins.specs, customSpecs],
  );

  // Re-read the project whenever the file map changes. This is what makes an
  // edit in the code view, a git restore and a branch switch all land in the
  // canvas through one path instead of three.
  useEffect(() => {
    if (!session.ready) return;
    useBuilder.getState().syncFromVfs();
  }, [rev, session.ready]);

  // A full reload (branch switch, restore, conflict reload) replaces every file,
  // so the canvas must re-read the page rather than keep its in-memory copy.
  useEffect(() => {
    if (!session.ready) return;
    useBuilder.getState().requestReload();
  }, [generation, session.ready]);

  // A diff is opened from the Source Control panel, which is reachable from every
  // view — including Design, which has nowhere to show it. Reveal the code pane
  // rather than let the click look like it did nothing.
  useEffect(() => {
    if (activeDiff && useBuilder.getState().view === 'design') setView('split');
  }, [activeDiff, setView]);

  const onEditorReady = useCallback((instance: Editor) => {
    setEditor(instance);
    const sync = () => {
      setCanUndo(instance.UndoManager.hasUndo());
      setCanRedo(instance.UndoManager.hasRedo());
    };
    instance.on('update', sync);
    instance.on('undo redo', sync);
  }, []);

  // Publish = ship this branch to `release`, which builds and deploys. The
  // working draft is flushed and committed first so what ships is what the
  // author sees, then a non-release branch completes through the merge dialog.
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
        expectBuild(siteId);
        setSidebarView('deploy');
      } else {
        setPublishMerge(true);
      }
    },
    onError: (e) => {
      if (e instanceof ApiError && e.status === 409) {
        toast.error(t('ide.git.resolveInScm'));
        setSidebarView('scm');
      } else {
        toast.error(t('errors.generic'));
      }
    },
  });

  // The plugin catalogue is part of the wait: the canvas registers its component
  // types once, at creation, so opening before the specs arrive would give this
  // session a palette missing every plugin block.
  if (site.isLoading || plugins.isLoading || !session.ready) {
    return <CenteredSpinner label={t('common.loading')} />;
  }

  const showCanvas = view !== 'code';
  const showCode = view !== 'design';

  return (
    <div className="flex h-[calc(100vh-3.5rem)] flex-col">
      {/* Toolbar */}
      <div className="flex h-12 shrink-0 items-center gap-2 border-b bg-card px-3">
        <Link to={sitesPath}>
          <Button size="icon" variant="ghost">
            <ArrowLeft className="h-4 w-4" />
          </Button>
        </Link>
        <span className="font-medium">{site.data?.name}</span>
        <span className="text-xs text-muted-foreground">{t('sites.modeStaticPrerender')}</span>

        {/* The canvas holds something that is not a page — a shared region or a
            component template. Without this the author is editing a header that
            appears on every page while the panel that names pages shows none of
            them selected, and the way back is not obvious. */}
        {activeKind !== 'page' && (
          <button
            type="button"
            onClick={() => {
              const home = project ? homePage(project.manifest).slug : null;
              if (home) useBuilder.getState().setActiveSlug(home);
            }}
            className="flex items-center gap-1 rounded-md border border-primary/40 bg-primary/10 px-2 py-1 text-xs text-primary"
            title={t('builder.regions.backToPage')}
          >
            {activeKind === 'region' ? (
              <LayoutTemplate className="h-3.5 w-3.5" />
            ) : (
              <Puzzle className="h-3.5 w-3.5" />
            )}
            {t('builder.regions.editing', {
              label:
                activeKind === 'region'
                  ? (project?.regions.find((r) => r.entry.slug === activeSlug)?.entry.label ??
                    activeSlug)
                  : (project?.components.find((c) => c.name === activeSlug)?.label ?? activeSlug),
            })}
          </button>
        )}

        <div className="mx-2 flex items-center gap-1 rounded-md border bg-muted p-0.5">
          {VIEWS.map(({ id, icon: Icon, labelKey }) => (
            <button
              key={id}
              type="button"
              title={t(labelKey)}
              onClick={() => setView(id)}
              className={cn(
                'flex h-7 w-8 items-center justify-center rounded',
                view === id ? 'bg-background shadow-sm' : 'text-muted-foreground',
              )}
            >
              <Icon className="h-4 w-4" />
            </button>
          ))}
        </div>

        <Button
          size="icon"
          variant="ghost"
          disabled={!canUndo}
          onClick={() => editor?.UndoManager.undo()}
          title={t('actions.undo')}
        >
          <Undo2 className="h-4 w-4" />
        </Button>
        <Button
          size="icon"
          variant="ghost"
          disabled={!canRedo}
          onClick={() => editor?.UndoManager.redo()}
          title={t('actions.redo')}
        >
          <Redo2 className="h-4 w-4" />
        </Button>

        <Button
          size="icon"
          variant="ghost"
          onClick={() => setAiOpen(true)}
          title={t('builder.ai.title')}
        >
          <Sparkles className="h-4 w-4" />
        </Button>

        <div className="flex-1" />
        <span className="text-xs text-muted-foreground">
          {dirty ? t('common.saving') : session.status}
        </span>
        <Button
          onClick={() => publish.mutate()}
          disabled={publish.isPending || !!conflict || session.switching}
        >
          <Rocket className="h-4 w-4" /> {t('ide.git.publishToRelease')}
        </Button>
      </div>

      {/* Double-clicking an image in the canvas opens the DCMS media library. */}
      <AssetsBridge editor={editor} />

      {/* Media stored as its published URL is fetched so it renders here too. */}
      <MediaBridge editor={editor} />

      {/* Plugin placeholders show real tenant content instead of a blank box. */}
      <PreviewBridge editor={editor} />

      {/* The page's shared header/footer regions, drawn around it. */}
      <RegionChrome editor={editor} />

      {/* While a component template is open, its empty bound elements say what
          they are bound to instead of rendering as blank boxes. */}
      <TemplateHints editor={editor} />

      <AiPanel open={aiOpen} onOpenChange={setAiOpen} specs={specs} />

      <MergeDialog
        siteId={siteId}
        head={branch}
        open={publishMerge}
        onOpenChange={setPublishMerge}
        onMerged={() => {
          // See IdePage: the build this merge causes is created by the push webhook after the
          // merge returns, so the refetch below cannot see it yet. The marker keeps the
          // Deployments panel looking until it appears.
          expectBuild(siteId);
          queryClient.invalidateQueries({ queryKey: ['git-history', siteId] });
          queryClient.invalidateQueries({ queryKey: ['git-changes', siteId] });
          queryClient.invalidateQueries({ queryKey: ['site-builds', siteId] });
          setSidebarView('deploy');
        }}
      />

      {/* Someone else changed a file we also edited (autosave is paused until reload), or --
          via the site hub -- committed to this branch at all. Both are fixed by reloading. */}
      {(conflict || branchMoved) && (
        <Banner
          tone="destructive"
          icon={<AlertTriangle className="h-4 w-4 shrink-0" />}
          message={
            conflict
              ? t('ide.conflictWarning', { files: conflict.join(', ') })
              : t('ide.branchMovedWarning', { branch })
          }
          action={
            <Button size="sm" variant="outline" onClick={() => session.openBranch(branch)}>
              <RefreshCw className="h-4 w-4" /> {t('ide.reloadLatest')}
            </Button>
          }
        />
      )}

      {/* An unreadable site.json is only fixable in the code view, so say so. */}
      {projectError && (
        <Banner
          tone="warning"
          icon={<AlertTriangle className="h-4 w-4 shrink-0" />}
          message={t('builder.projectError', { error: projectError })}
          action={
            <Button size="sm" variant="outline" onClick={() => setView('code')}>
              <Code2 className="h-4 w-4" /> {t('builder.openCode')}
            </Button>
          }
        />
      )}

      {/* Workspace: sidebar | canvas (+ code) | inspector. */}
      <div className="relative flex min-h-0 flex-1">
        <div style={{ width: sidebarWidth }} className="min-w-0 shrink-0 border-r bg-card">
          <BuilderSidebar
            siteId={siteId}
            view={sidebarView}
            onViewChange={setSidebarView}
            editor={editor}
          />
        </div>
        <Resizer onDelta={(dx) => setSidebarWidth(sidebarWidth + dx)} onReset={resetSidebarWidth} />

        <div className="flex min-w-0 flex-1">
          {/*
            The canvas stays mounted in Code view and is merely hidden. Unmounting
            it destroys the GrapesJS editor, and the panels, the AI dialog and the
            preview bridge all hold that same object — so a view switch used to
            take the page down. Keeping it alive also preserves the undo history
            and the scroll position across a switch.
          */}
          <div className={cn('min-w-0 flex-1 bg-muted/40', !showCanvas && 'hidden')}>
            {project ? (
              <BuilderCanvas
                onReady={onEditorReady}
                onTeardown={() => setEditor(null)}
                enabledPluginIds={plugins.enabledPluginIds}
                pluginSpecs={plugins.specs}
                customSpecs={customSpecs}
              />
            ) : (
              <div className="flex h-full items-center justify-center p-6 text-sm text-muted-foreground">
                {t('builder.noProject')}
              </div>
            )}
          </div>
          {/* Split view: the code pane keeps a dragged width, the canvas takes the
              rest. In Code view it is the only pane, so it simply fills. */}
          {showCode && showCanvas && (
            <Resizer
              onDelta={(dx) => setCodeWidth(codeWidth - dx)}
              onReset={resetCodeWidth}
              ariaLabel={t('builder.resizeCode')}
            />
          )}
          {showCode && (
            <div
              style={showCanvas ? { width: codeWidth } : undefined}
              // max-w wins over the inline width, so a width stored on a wide
              // screen can never squeeze the canvas out of existence on a narrow one.
              className={cn('min-w-0 border-l', showCanvas ? 'max-w-[75%] shrink-0' : 'flex-1')}
            >
              <CodeView specs={specs} />
            </div>
          )}
        </div>

        <Resizer
          onDelta={(dx) => setInspectorWidth(inspectorWidth - dx)}
          onReset={resetInspectorWidth}
        />
        <div style={{ width: inspectorWidth }} className="min-w-0 shrink-0 border-l bg-card">
          <Inspector editor={editor} />
        </div>
      </div>
    </div>
  );
}

function Banner({
  tone,
  icon,
  message,
  action,
}: {
  tone: 'destructive' | 'warning';
  icon: React.ReactNode;
  message: string;
  action: React.ReactNode;
}) {
  return (
    <div
      className={cn(
        'flex shrink-0 items-center gap-2 border-b px-3 py-2 text-sm',
        tone === 'destructive'
          ? 'bg-destructive/10 text-destructive'
          : 'bg-amber-500/10 text-amber-700 dark:text-amber-400',
      )}
    >
      {icon}
      <span className="min-w-0 flex-1">{message}</span>
      {action}
    </div>
  );
}


