import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTypeProblems } from './diagnostics/useTypeProblems';
import { Link } from '@tanstack/react-router';
import {
  AlertTriangle,
  ArrowLeft,
  Eye,
  EyeOff,
  RefreshCw,
  Rocket,
  RotateCcw,
  Terminal,
  X,
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Breadcrumbs } from './Breadcrumbs';
import { BottomPanel } from './panel/BottomPanel';
import { usePanelState } from './panel/panelState';
import { appendOutput } from './panel/output';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button, CenteredSpinner } from '@dcms/ui';
import { ApiError, api } from '../../lib/api';
import { useAuth } from '../../useAuth';
import {
  BinaryFileView,
  DiffEditor,
  EditorTabs,
  MergeDialog,
  MonacoEditor,
  RELEASE_BRANCH,
  Resizer,
  StatusBar,
  gitApi,
  ideApi,
  isBinaryPath,
  loadOpenDocs,
  saveOpenDocs,
  useDraftSession,
  useSiteLiveUpdates,
  useStoredWidth,
  useVfs,
} from '../site-source';
import { type IdeCommand, modifierLabel } from './commands';
import { IdeCommandPalette, type PaletteMode } from './IdeCommandPalette';
import { IdeSidebar, type SidebarView } from './IdeSidebar';
import { PreviewPane } from './PreviewPane';
import { ShortcutSheet } from './ShortcutSheet';
import { useIdeShortcuts } from './useIdeShortcuts';
import { usePreview } from './preview/usePreview';
import { StarterPicker, type StarterFlavor } from './StarterPicker';
import { STARTER_FILES } from './starter';
import { ensurePaletteTypes } from './types/palette';

const sitesPath: string = '/sites';

interface SiteData {
  name: string;
}

// The code IDE surface for Mode B (ReactApp) sites. The file map is a per-user,
// per-branch working draft in the backend (autosaved, race-free); commits, branches,
// diffs and merges are driven from the Source Control view in the left sidebar.
// Publishing commits the draft to the `release` branch, which builds + deploys.
export function IdePage({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const dirty = useVfs((s) => s.dirty);
  const branch = useVfs((s) => s.branch);
  const openTabs = useVfs((s) => s.openTabs);
  const activePath = useVfs((s) => s.activePath);
  const activeDiff = useVfs((s) => s.activeDiff);
  const openDiffs = useVfs((s) => s.openDiffs);
  const conflict = useVfs((s) => s.conflict);
  const [showPreview, setShowPreview] = useState(true);
  const [sidebarView, setSidebarView] = useState<SidebarView>('files');
  // When publishing from a non-release branch, we open the merge-into-release dialog.
  const [publishMerge, setPublishMerge] = useState(false);
  // Shown for a brand-new (empty) site so the user picks what to scaffold.
  const [pickStarter, setPickStarter] = useState(false);
  const [previewNonce, setPreviewNonce] = useState(0);
  const [palette, setPalette] = useState<{ open: boolean; mode: PaletteMode }>({
    open: false,
    mode: 'files',
  });
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  // Driven here rather than inside PreviewPane: the Problems view lists the messages from this
  // same build, and a second usePreview would be a second worker bundling the same project.
  const preview = usePreview(showPreview, siteId, previewNonce);
  // Type problems come from Monaco's worker, which is attached whether or not the preview is,
  // so the Problems panel has something true to say even with the preview closed.
  const typeProblems = useTypeProblems();
  const problems = useMemo(
    () => [...typeProblems, ...preview.problems],
    [typeProblems, preview.problems],
  );
  // Marks the site whose persisted tabs have been restored — gates tab autosave so
  // we never write the previous site's tabs under a newly-selected site's key.
  const restoredFor = useRef<string | null>(null);
  const activeDiffTab = activeDiff ? openDiffs.find((d) => d.path === activeDiff) : undefined;
  const files = useVfs((s) => s.files);

  // Resizable layout: the sidebar view and preview panels remember their width
  // (px) in localStorage; double-clicking a divider restores the default.
  const panel = usePanelState();
  const [panelHeight, setPanelHeight, resetPanelHeight] = useStoredWidth(
    'dcms.ide.panelHeight',
    220,
    100,
    700,
  );
  const [sidebarWidth, setSidebarWidth, resetSidebarWidth] = useStoredWidth(
    'dcms.ide.sidebarWidth',
    240,
    160,
    520,
  );
  const [previewWidth, setPreviewWidth, resetPreviewWidth] = useStoredWidth(
    'dcms.ide.previewWidth',
    560,
    320,
    1100,
  );

  const site = useQuery({
    queryKey: ['site', siteId],
    queryFn: () => api.get<SiteData>(`/admin/sites/${siteId}`),
  });

  // Live deployments and commits for everyone with this site open. `sub` identifies this user
  // so the hook can tell your own publish (which already toasted) from a colleague's.
  const { user } = useAuth();
  const [branchMoved, setBranchMoved] = useState(false);
  const [draftMovedPaths, setDraftMovedPaths] = useState<string[] | null>(null);
  const draftVersion = useVfs((s) => s.version);
  useSiteLiveUpdates({
    siteId,
    branch,
    myUserId: user?.profile.sub,
    // Somebody else committed to the branch in this editor. The banner the conflict path
    // already renders is the right place to say so, and it comes with the reload button.
    onCommit: () => setBranchMoved(true),
    /*
     * Your own draft, written from somewhere else — a second tab, or the AI agent.
     *
     * This used to raise a banner offering a reload, which was the wrong default: the
     * overwhelming majority of these messages are about files this tab has never touched, and
     * answering "somebody saved another file" with "reload and lose what you typed" is a
     * false positive with a destructive remedy.
     *
     * So pull and merge instead. `syncFromServer` adopts anything untouched here, reconciles
     * anything that happens to match, and hands back only the paths that genuinely diverged.
     * The banner appears for those, and only those — usually none.
     */
    draftVersion: draftVersion,
    onDraftChanged: () => {
      void session.syncFromServer().then((diverged) => {
        setDraftMovedPaths(diverged.length > 0 ? diverged : null);
      });
    },
  });

  // A branch switch or a reload settles both banners; clearing on `branch` covers both.
  useEffect(() => {
    setBranchMoved(false);
    setDraftMovedPaths(null);
  }, [branch]);

  // The shared working-draft session: load a branch, autosave granular deltas,
  // detect conflicts. `seed` returns null on purpose — a brand-new Mode B site
  // asks the user what to scaffold rather than being given one silently.
  const session = useDraftSession({
    siteId,
    seed: () => null,
    onLoaded: (loadedBranch, isEmpty) => {
      restoredFor.current = siteId;
      if (isEmpty) {
        setPickStarter(true);
        return;
      }
      // Reopen the documents that were open last time for this site + branch.
      const saved = loadOpenDocs(siteId, loadedBranch);
      if (saved) useVfs.getState().restoreSession(saved.openTabs, saved.activePath);
    },
  });

  // Seed a brand-new site's workspace from the chosen starter flavor. Everything
  // but "empty" is generated from the tenant's content API by the backend.
  const scaffold = useMutation({
    mutationFn: (flavor: StarterFlavor) =>
      flavor === 'empty'
        ? Promise.resolve({ files: { ...STARTER_FILES } })
        : ideApi.scaffold(siteId, flavor),
    onSuccess: ({ files }) => {
      useVfs.getState().seedStarter(files);
      setPickStarter(false);
    },
    onError: () => {
      toast.error(t('errors.generic'));
      // Fall back to an empty project so the IDE is never left blank.
      useVfs.getState().seedStarter({ ...STARTER_FILES });
      setPickStarter(false);
    },
  });

  /*
   * Re-pull the DCMS-generated layer (openapi.json + the typed client under
   * src/api/) from the tenant's current content API. Those files are emitted once
   * when the site is scaffolded, so they drift the moment a plugin is installed or
   * reconfigured — this is how the author picks the new endpoints up.
   *
   * Written through writeFile rather than seedStarter/importFiles: it must land as
   * ordinary pending edits (so the normal autosave + git commit carries them) and
   * must not steal the active tab.
   */
  const refreshGenerated = useMutation({
    mutationFn: () => ideApi.regenerate(siteId),
    onSuccess: ({ files }) => {
      const vfs = useVfs.getState();
      const changed = Object.entries(files).filter(
        ([path, content]) => vfs.files[path] !== content,
      );
      for (const [path, content] of changed) vfs.writeFile(path, content);
      const message =
        changed.length === 0
          ? t('ide.generatedUpToDate')
          : t('ide.generatedRefreshed', { count: changed.length });
      toast.success(message);
      // …and again where it will still be readable in ten minutes. A toast answers "what just
      // happened"; the Output tab answers "what happened while I was reading the code".
      appendOutput('workspace', message);
      for (const [path] of changed) appendOutput('workspace', `regenerated ${path}`);
    },
    onError: () => {
      toast.error(t('errors.generic'));
      appendOutput('workspace', t('ide.refreshGenerated'), 'error');
    },
  });

  // Mode B typings are opted into here rather than by the shared Monaco setup, so
  // the Mode A builder never pays for the React dependency palette.
  useEffect(() => {
    ensurePaletteTypes();
  }, []);

  // Persist the open documents (per site + branch) whenever the tab set or focus
  // changes, but only once this site's own session has been restored (see restoredFor).
  useEffect(() => {
    if (!session.ready || restoredFor.current !== siteId) return;
    saveOpenDocs(siteId, branch, { openTabs, activePath });
  }, [openTabs, activePath, session.ready, siteId, branch]);

  // Wipe the tenant's preview sandbox (test form submissions, visitors, chats),
  // then force the iframe to rebuild so it reflects the cleared state.
  const resetSandbox = useMutation({
    mutationFn: () => api.post(`/admin/sites/${siteId}/preview/sandbox/reset`),
    onSuccess: () => {
      setPreviewNonce((n) => n + 1);
      toast.success(t('ide.sandboxReset'));
      appendOutput('workspace', t('ide.sandboxReset'));
    },
    onError: () => {
      toast.error(t('errors.generic'));
      appendOutput('workspace', t('ide.resetSandbox'), 'error');
    },
  });

  // Switching branches flushes the branch being left; the shared session handles
  // that. An unresolved conflict blocks the switch, because the draft the switch
  // would flush is the one the server has already rejected.
  const switchBranch = (b: string) => {
    if (b === branch || session.switching) return;
    if (conflict) {
      toast.error(t('ide.git.resolveInScm'));
      return;
    }
    session.openBranch(b);
  };

  // Publish = ship the current branch to `release` (→ build + deploy). Commit the
  // working draft first (reconciling a moved branch); on `release` that commit alone
  // builds, otherwise open the merge-into-release dialog to complete the publish.
  const publish = useMutation({
    mutationFn: async () => {
      await session.flush(); // flush pending edits first (throws on conflict)
      await gitApi.commit(siteId, { branch, message: `Publish ${branch}` });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['git-changes', siteId] });
      queryClient.invalidateQueries({ queryKey: ['git-history', siteId] });
      if (branch === RELEASE_BRANCH) {
        toast.success(t('editor.publishQueued'));
        appendOutput('git', t('editor.publishQueued'));
        setSidebarView('deploy');
      } else {
        setPublishMerge(true);
      }
    },
    onError: (e) => {
      if (e instanceof ApiError && e.status === 409) {
        // Draft save conflict, or the branch moved and the same files were edited on
        // both sides — either way, resolve it in Source Control before publishing.
        toast.error(t('ide.git.resolveInScm'));
        appendOutput('git', t('ide.git.resolveInScm'), 'error');
        setSidebarView('scm');
      } else {
        toast.error(t('errors.generic'));
        appendOutput('git', t('errors.generic'), 'error');
      }
    },
  });

  /*
   * The palette's command list, and the keyboard.
   *
   * Both are declared here rather than inside the palette because every one of these actions is
   * this component's state or one of its mutations — a palette that owned them would need every
   * handler passed in anyway, and the list would then live in two places.
   */
  const mod = modifierLabel();
  const openPalette = useCallback((mode: PaletteMode) => setPalette({ open: true, mode }), []);

  const paletteCommands: IdeCommand[] = useMemo(() => {
    const view = (id: SidebarView, label: string, shortcut?: string, keywords?: string) => ({
      id: `view.${id}`,
      label,
      shortcut,
      keywords,
      run: () => setSidebarView(id),
    });

    return [
      view('files', t('ide.explorer'), undefined, 'explorer tree'),
      view('search', t('ide.search.title'), `${mod} ⇧ F`, 'find grep'),
      view('scm', t('ide.git.title'), `${mod} ⇧ G`, 'git commit branch diff'),
      view('deploy', t('ide.deploy.title'), undefined, 'builds releases'),
      view('agent', t('ide.agent.title'), undefined, 'ai chat'),
      /*
       * The bottom panel, one command per tab.
       *
       * Four entries rather than one "toggle panel", because the palette is how somebody finds
       * a surface they have not met yet — and "Problems" is a word they will search for while
       * "panel" is not.
       */
      {
        id: 'panel.problems',
        label: t('ide.problems.title'),
        shortcut: `${mod} ⇧ M`,
        keywords: 'errors warnings build panel',
        run: () => panel.show('problems'),
      },
      {
        id: 'panel.output',
        label: t('ide.panel.tabs.output'),
        keywords: 'log panel workspace',
        run: () => panel.show('output'),
      },
      {
        id: 'panel.console',
        label: t('ide.panel.tabs.console'),
        keywords: 'log panel runtime errors',
        run: () => panel.show('console'),
      },
      {
        id: 'panel.build',
        label: t('ide.panel.tabs.build'),
        keywords: 'log panel history compile',
        run: () => panel.show('build'),
      },
      {
        id: 'tabs.closeOthers',
        label: t('ide.tabs.closeOthers'),
        keywords: 'tabs editor tidy',
        disabled: !activePath,
        run: () => useVfs.getState().closeOthers(activePath),
      },
      {
        id: 'preview.toggle',
        label: showPreview ? t('ide.hidePreview') : t('ide.showPreview'),
        shortcut: `${mod} \\`,
        run: () => setShowPreview((v) => !v),
      },
      {
        id: 'preview.refresh',
        label: t('ide.refresh'),
        keywords: 'rebuild bundle',
        // Disabled rather than hidden while the preview is off: the command is not gone, it is
        // waiting on something the reader can turn on from the line above.
        disabled: !showPreview,
        run: () => preview.refresh(),
      },
      {
        id: 'draft.save',
        label: t('ide.shortcuts.saveNow'),
        shortcut: `${mod} S`,
        run: () => void session.flush().catch(() => toast.error(t('ide.git.resolveInScm'))),
      },
      {
        id: 'tab.close',
        label: t('ide.shortcuts.closeTab'),
        disabled: !activePath,
        run: () => activePath && useVfs.getState().closeTab(activePath),
      },
      {
        id: 'generated.refresh',
        label: t('ide.refreshGenerated'),
        keywords: 'api client openapi types',
        disabled: refreshGenerated.isPending,
        run: () => refreshGenerated.mutate(),
      },
      {
        id: 'publish',
        label: t('ide.git.publishToRelease'),
        keywords: 'deploy ship release',
        disabled: publish.isPending || !!conflict || session.switching,
        run: () => publish.mutate(),
      },
      {
        id: 'sandbox.reset',
        label: t('ide.resetSandbox'),
        keywords: 'clear test data',
        disabled: resetSandbox.isPending,
        run: () => resetSandbox.mutate(),
      },
      {
        id: 'shortcuts',
        label: t('ide.shortcuts.title'),
        shortcut: `${mod} ?`,
        keywords: 'keyboard keys help',
        run: () => setShortcutsOpen(true),
      },
    ];
  }, [
    t,
    mod,
    panel,
    showPreview,
    activePath,
    conflict,
    session,
    preview,
    refreshGenerated,
    publish,
    resetSandbox,
  ]);

  useIdeShortcuts(
    useMemo(
      () => [
        { key: 'p', run: () => openPalette('files') },
        { key: 'p', shift: true, run: () => openPalette('commands') },
        { key: 'f', shift: true, run: () => setSidebarView('search') },
        { key: 'g', shift: true, run: () => setSidebarView('scm') },
        { key: 'm', shift: true, run: () => panel.show('problems') },
        // The backtick is the bottom panel everywhere a bottom panel exists. This hook treats
        // Meta and Control as one modifier (see its notes), so this answers to both — which
        // matters here because macOS claims ⌘` for window cycling and will not give it up.
        { key: '`', run: () => panel.toggle() },
        { key: '\\', run: () => setShowPreview((v) => !v) },
        /*
         * The shortcut sheet is on ⌘? and NOT on ⌘/ — Monaco binds ⌘/ to toggle-comment, which
         * is one of the handful of editor shortcuts people use without thinking. Taking it would
         * be a regression dressed as a feature.
         *
         * Two entries because `?` is Shift+/ on some layouts and its own key on others, so which
         * of the two `event.key` reports is not ours to decide.
         */
        { key: '?', shift: true, run: () => setShortcutsOpen(true) },
        { key: '/', shift: true, run: () => setShortcutsOpen(true) },
        {
          key: 's',
          // The draft autosaves; this only makes the wait explicit. Bound anyway because ⌘S is
          // reflex, and the alternative is the browser's Save Page As dialog over the editor.
          run: () => void session.flush().catch(() => toast.error(t('ide.git.resolveInScm'))),
        },
        /*
         * ⌘W is deliberately NOT bound. Chrome refuses `preventDefault` on it, so binding it
         * would close the editor's tab AND the browser's — losing unsaved work to a shortcut
         * that appeared to be ours. Closing a tab stays a palette command and a click on the ×.
         */
      ],
      // `panel` is here because its `toggle` closes over the panel's own open/tab state: a
      // stale one would toggle against what the panel looked like at the last render, which
      // shows up as a shortcut that opens the panel when it should close it.
      [openPalette, panel, session, t],
    ),
  );

  if (site.isLoading || !session.ready) return <CenteredSpinner label={t('common.loading')} />;

  return (
    <div className="flex h-[calc(100vh-3.5rem)] flex-col">
      <StarterPicker
        open={pickStarter}
        pending={scaffold.isPending}
        onPick={(f) => scaffold.mutate(f)}
      />

      <IdeCommandPalette
        open={palette.open}
        mode={palette.mode}
        onOpenChange={(open) => setPalette((p) => ({ ...p, open }))}
        files={Object.keys(files)}
        commands={paletteCommands}
        onOpenFile={(path) => useVfs.getState().open(path)}
      />
      <ShortcutSheet open={shortcutsOpen} onOpenChange={setShortcutsOpen} />

      {/* Toolbar */}
      <div className="flex h-12 shrink-0 items-center gap-2 border-b bg-card px-3">
        <Link to={sitesPath}>
          <Button size="icon" variant="ghost">
            <ArrowLeft className="h-4 w-4" />
          </Button>
        </Link>
        <span className="font-medium">{site.data?.name}</span>
        <span className="text-xs text-muted-foreground">{t('sites.modeReactApp')}</span>

        <div className="flex-1" />
        <span className="text-xs text-muted-foreground">
          {session.saving || dirty ? t('common.saving') : session.status}
        </span>
        <Button
          variant="ghost"
          onClick={() => refreshGenerated.mutate()}
          disabled={refreshGenerated.isPending}
          title={t('ide.refreshGeneratedHint')}
        >
          <RefreshCw className="h-4 w-4" /> {t('ide.refreshGenerated')}
        </Button>
        <Button
          variant="ghost"
          onClick={() => resetSandbox.mutate()}
          disabled={resetSandbox.isPending}
          title={t('ide.resetSandboxHint')}
        >
          <RotateCcw className="h-4 w-4" /> {t('ide.resetSandbox')}
        </Button>
        <Button
          variant="ghost"
          onClick={() => openPalette('commands')}
          title={t('ide.palette.runCommandHint', { keys: `${mod} ⇧ P` })}
        >
          <Terminal className="h-4 w-4" /> {t('ide.palette.runCommand')}
        </Button>
        <Button variant="outline" onClick={() => setShowPreview((v) => !v)}>
          {showPreview ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
          {showPreview ? t('ide.hidePreview') : t('ide.showPreview')}
        </Button>
        <Button
          onClick={() => publish.mutate()}
          disabled={publish.isPending || !!conflict || session.switching}
        >
          <Rocket className="h-4 w-4" /> {t('ide.git.publishToRelease')}
        </Button>
      </div>

      {/* Publishing from a non-release branch: complete the merge into release. */}
      <MergeDialog
        siteId={siteId}
        head={branch}
        open={publishMerge}
        onOpenChange={setPublishMerge}
        onMerged={() => {
          // The merge has landed, but the build row it causes is written by the push webhook
          // a moment later, so the refetch this triggers will not see it. That is the site
          // hub's job: `BuildChanged` arrives when the build does. The Deployments panel used
          // to guess at it with a 2.5-second poll and a 90-second window; it no longer has to.
          queryClient.invalidateQueries({ queryKey: ['git-history', siteId] });
          queryClient.invalidateQueries({ queryKey: ['git-changes', siteId] });
          queryClient.invalidateQueries({ queryKey: ['site-builds', siteId] });
          setSidebarView('deploy');
        }}
      />

      {/*
        Your own draft moved somewhere else — a second tab, or the AI agent.
        Deliberately NOT the destructive-red conflict banner: nothing is broken and nothing is
        blocked, the editor simply knows something you do not yet. It names the files, because
        "the draft changed" is a banner nobody can act on while "src/App.tsx changed" tells you
        at once whether it collides with what you are doing.
      */}
      {draftMovedPaths && !conflict ? (
        <div className="flex shrink-0 items-center gap-2 border-b bg-[hsl(var(--warning)/0.12)] px-3 py-2 text-sm">
          <AlertTriangle className="h-4 w-4 shrink-0 text-[hsl(var(--warning))]" aria-hidden />
          <span className="min-w-0 flex-1">
            {t('ide.draftMovedWarning', { files: draftMovedPaths.slice(0, 3).join(', ') })}
            {draftMovedPaths.length > 3
              ? t('ide.andMore', { count: draftMovedPaths.length - 3 })
              : null}
          </span>
          <Button
            size="sm"
            variant="outline"
            onClick={() => session.openBranch(branch)}
            disabled={session.switching}
          >
            <RefreshCw className="h-4 w-4" /> {t('ide.reloadLatest')}
          </Button>
          <button
            type="button"
            onClick={() => setDraftMovedPaths(null)}
            aria-label={t('actions.dismiss')}
            className="shrink-0 rounded p-1 text-muted-foreground hover:text-foreground"
          >
            <X className="h-4 w-4" aria-hidden />
          </button>
        </div>
      ) : null}

      {/* Conflict banner: someone else changed a file we also edited, or -- via the site hub --
          committed to the branch this editor has open at all. Both have the same remedy. */}
      {(conflict || branchMoved) && (
        <div className="flex shrink-0 items-center gap-2 border-b bg-destructive/10 px-3 py-2 text-sm text-destructive">
          <AlertTriangle className="h-4 w-4 shrink-0" />
          <span className="min-w-0 flex-1">
            {conflict
              ? t('ide.conflictWarning', { files: conflict.join(', ') })
              : t('ide.branchMovedWarning', { branch })}
          </span>
          <Button
            size="sm"
            variant="outline"
            onClick={() => session.openBranch(branch)}
            disabled={session.switching}
          >
            <RefreshCw className="h-4 w-4" /> {t('ide.reloadLatest')}
          </Button>
        </div>
      )}

      {/* Workspace: sidebar (explorer / source control) | editor | preview.
          The dividers between panels are draggable; double-click resets a width. */}
      <div className="relative flex min-h-0 flex-1">
        <IdeSidebar
          siteId={siteId}
          siteName={site.data?.name}
          branch={branch}
          view={sidebarView}
          onViewChange={setSidebarView}
          onSwitchBranch={switchBranch}
          onReload={() => session.openBranch(branch)}
          onRestored={(files, version, hashes) => useVfs.getState().load(files, version, hashes)}
          viewWidth={sidebarWidth}
        />
        <Resizer
          ariaLabel={t('ide.resizeSidebar')}
          onDelta={(dx) => setSidebarWidth((w) => w + dx)}
          onReset={resetSidebarWidth}
        />
        <div className="flex min-w-0 flex-1 flex-col">
          <EditorTabs />
          <Breadcrumbs path={activeDiff ? (activeDiffTab?.path ?? null) : activePath} />
          <div className="relative min-h-0 flex-1">
            <MonacoEditor key={siteId} />
            {activeDiffTab && (
              <div className="absolute inset-0 bg-background">
                <DiffEditor
                  path={activeDiffTab.path}
                  original={activeDiffTab.original}
                  modified={activeDiffTab.modified}
                />
              </div>
            )}
            {!activeDiff && activePath && isBinaryPath(activePath) && (
              <div className="absolute inset-0">
                <BinaryFileView />
              </div>
            )}
          </div>
          {/* Under the editor group, not under the whole workspace: everything in the panel is
              about the code, and stretching it beneath the preview would put build errors under
              a rendered page they have nothing to do with. */}
          <BottomPanel
            state={panel}
            height={panelHeight}
            onHeightDelta={(dy) => setPanelHeight((h) => h + dy)}
            onHeightReset={resetPanelHeight}
            problems={problems}
            building={preview.building}
            previewEnabled={showPreview}
            onEnablePreview={() => setShowPreview(true)}
          />
        </div>
        {showPreview && (
          <>
            <Resizer
              ariaLabel={t('ide.resizePreview')}
              onDelta={(dx) => setPreviewWidth((w) => w - dx)}
              onReset={resetPreviewWidth}
            />
            <div className="shrink-0 border-l" style={{ width: `${previewWidth}px` }}>
              <PreviewPane preview={preview} />
            </div>
          </>
        )}
      </div>

      <StatusBar
        siteId={siteId}
        branch={branch}
        dirty={session.saving || dirty}
        onOpenScm={() => setSidebarView('scm')}
      />
    </div>
  );
}
