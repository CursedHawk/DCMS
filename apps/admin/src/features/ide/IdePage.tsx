import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { AlertTriangle, ArrowLeft, Eye, EyeOff, RefreshCw, Rocket, RotateCcw } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '../../components/ui/button';
import { CenteredSpinner } from '../../components/ui/spinner';
import { ApiError, api } from '../../lib/api';
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
  useStoredWidth,
  useVfs,
} from '../site-source';
import { IdeSidebar, type SidebarView } from './IdeSidebar';
import { PreviewPane } from './PreviewPane';
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
  // Marks the site whose persisted tabs have been restored — gates tab autosave so
  // we never write the previous site's tabs under a newly-selected site's key.
  const restoredFor = useRef<string | null>(null);
  const activeDiffTab = activeDiff ? openDiffs.find((d) => d.path === activeDiff) : undefined;

  // Resizable layout: the sidebar view and preview panels remember their width
  // (px) in localStorage; double-clicking a divider restores the default.
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
      const changed = Object.entries(files).filter(([path, content]) => vfs.files[path] !== content);
      for (const [path, content] of changed) vfs.writeFile(path, content);
      toast.success(
        changed.length === 0
          ? t('ide.generatedUpToDate')
          : t('ide.generatedRefreshed', { count: changed.length }),
      );
    },
    onError: () => toast.error(t('errors.generic')),
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
    },
    onError: () => toast.error(t('errors.generic')),
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
        setSidebarView('scm');
      } else {
        toast.error(t('errors.generic'));
      }
    },
  });

  if (site.isLoading || !session.ready) return <CenteredSpinner label={t('common.loading')} />;

  return (
    <div className="flex h-[calc(100vh-3.5rem)] flex-col">
      <StarterPicker open={pickStarter} pending={scaffold.isPending} onPick={(f) => scaffold.mutate(f)} />
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
        <Button variant="outline" onClick={() => setShowPreview((v) => !v)}>
          {showPreview ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
          {showPreview ? t('ide.hidePreview') : t('ide.showPreview')}
        </Button>
        <Button onClick={() => publish.mutate()} disabled={publish.isPending || !!conflict || session.switching}>
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
          queryClient.invalidateQueries({ queryKey: ['git-history', siteId] });
          queryClient.invalidateQueries({ queryKey: ['site-builds', siteId] });
          setSidebarView('deploy');
        }}
      />

      {/* Conflict banner: someone else changed a file we also edited. */}
      {conflict && (
        <div className="flex shrink-0 items-center gap-2 border-b bg-destructive/10 px-3 py-2 text-sm text-destructive">
          <AlertTriangle className="h-4 w-4 shrink-0" />
          <span className="min-w-0 flex-1">
            {t('ide.conflictWarning', { files: conflict.join(', ') })}
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
        </div>
        {showPreview && (
          <>
            <Resizer
              ariaLabel={t('ide.resizePreview')}
              onDelta={(dx) => setPreviewWidth((w) => w - dx)}
              onReset={resetPreviewWidth}
            />
            <div className="shrink-0 border-l" style={{ width: `${previewWidth}px` }}>
              <PreviewPane enabled={showPreview} siteId={siteId} refreshKey={previewNonce} />
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
