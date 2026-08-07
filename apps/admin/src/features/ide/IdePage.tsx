import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { AlertTriangle, ArrowLeft, Eye, EyeOff, RefreshCw, Rocket, RotateCcw } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '../../components/ui/button';
import { CenteredSpinner } from '../../components/ui/spinner';
import { ApiError, api } from '../../lib/api';
import { isBinaryPath } from './binary';
import { BinaryFileView } from './BinaryFileView';
import { RELEASE_BRANCH } from './constants';
import { DiffEditor } from './DiffEditor';
import { EditorTabs } from './EditorTabs';
import { gitApi } from './git';
import { ideApi } from './ide';
import { IdeSidebar, type SidebarView } from './IdeSidebar';
import { MergeDialog } from './MergeDialog';
import { MonacoEditor } from './MonacoEditor';
import { loadOpenDocs, saveOpenDocs } from './openDocs';
import { PreviewPane } from './PreviewPane';
import { Resizer, useStoredWidth } from './Resizer';
import { StarterPicker, type StarterFlavor } from './StarterPicker';
import { StatusBar } from './StatusBar';
import { STARTER_FILES } from './starter';
import { useVfs } from './vfs';

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
  const rev = useVfs((s) => s.rev);
  const branch = useVfs((s) => s.branch);
  const openTabs = useVfs((s) => s.openTabs);
  const activePath = useVfs((s) => s.activePath);
  const activeDiff = useVfs((s) => s.activeDiff);
  const openDiffs = useVfs((s) => s.openDiffs);
  const conflict = useVfs((s) => s.conflict);
  const [showPreview, setShowPreview] = useState(true);
  const [sidebarView, setSidebarView] = useState<SidebarView>('files');
  const [status, setStatus] = useState('');
  const [ready, setReady] = useState(false);
  const [switching, setSwitching] = useState(false);
  // When publishing from a non-release branch, we open the merge-into-release dialog.
  const [publishMerge, setPublishMerge] = useState(false);
  // Shown for a brand-new (empty) site so the user picks what to scaffold.
  const [pickStarter, setPickStarter] = useState(false);
  const [previewNonce, setPreviewNonce] = useState(0);
  const loadedFor = useRef<string | null>(null);
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

  // Load a branch's working draft into the vfs (seeding a starter for a brand-new,
  // empty site so the IDE is never blank). `undefined` lets the server pick the
  // site's default branch.
  const loadBranch = useMutation({
    mutationFn: (b?: string) => ideApi.load(siteId, b),
    onSuccess: (data) => {
      useVfs.getState().setBranch(data.branch);
      restoredFor.current = siteId;
      if (Object.keys(data.files).length > 0) {
        useVfs.getState().load(data.files, data.version, data.hashes);
        // Reopen the documents that were open last time for this site + branch.
        const saved = loadOpenDocs(siteId, data.branch);
        if (saved) useVfs.getState().restoreSession(saved.openTabs, saved.activePath);
      } else {
        // Brand-new site: let the user choose what to scaffold before seeding.
        setPickStarter(true);
      }
      setReady(true);
    },
    onError: () => toast.error(t('errors.loadFailed')),
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

  useEffect(() => {
    if (site.data && loadedFor.current !== siteId) {
      loadedFor.current = siteId;
      loadBranch.mutate(undefined);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [site.data, siteId]);

  // Granular save: flush only the changed/deleted files into the user's draft for
  // the current branch, each carrying the hash it was last synced at, so the same
  // account editing the same file+branch from two tabs is reported (409) instead of
  // clobbered. Edits to different files just merge.
  const save = useMutation({
    mutationFn: async () => {
      const delta = useVfs.getState().takeDelta();
      if (Object.keys(delta.put).length === 0 && delta.delete.length === 0) return;
      const res = await ideApi.saveFiles(siteId, useVfs.getState().branch, delta);
      if (res) useVfs.getState().reconcile(delta, res.version, res.hashes);
    },
    onSuccess: () => {
      setStatus(t('common.saved'));
      queryClient.invalidateQueries({ queryKey: ['git-changes', siteId] });
    },
    onError: (e) => {
      if (e instanceof ApiError && e.status === 409) {
        const paths = ((e.detail as { conflicts?: { path: string }[] })?.conflicts ?? []).map(
          (c) => c.path,
        );
        useVfs.getState().setConflict(paths);
      } else {
        toast.error(t('errors.generic'));
      }
    },
  });

  // Debounced autosave on any content change — paused while a conflict is unresolved.
  useEffect(() => {
    if (!ready || !dirty || conflict || loadedFor.current !== siteId) return;
    const id = setTimeout(() => save.mutate(), 1200);
    return () => clearTimeout(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rev, dirty, conflict, ready]);

  // Persist the open documents (per site + branch) whenever the tab set or focus
  // changes, but only once this site's own session has been restored (see restoredFor).
  useEffect(() => {
    if (!ready || restoredFor.current !== siteId) return;
    saveOpenDocs(siteId, branch, { openTabs, activePath });
  }, [openTabs, activePath, ready, siteId, branch]);

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

  // Switch branches without losing work: flush the current branch's draft first
  // (drafts are per-branch, so already-saved edits are safe), then load the target.
  // Blocked while a save conflict is unresolved.
  const switchBranch = async (b: string) => {
    if (b === branch || switching || loadBranch.isPending) return;
    if (conflict) {
      toast.error(t('ide.git.resolveInScm'));
      return;
    }
    setSwitching(true);
    try {
      if (dirty) await save.mutateAsync();
    } catch {
      // A save conflict leaves the draft as-is server-side; still allow the switch.
    }
    loadBranch.mutate(b, { onSettled: () => setSwitching(false) });
  };

  // Publish = ship the current branch to `release` (→ build + deploy). Commit the
  // working draft first (reconciling a moved branch); on `release` that commit alone
  // builds, otherwise open the merge-into-release dialog to complete the publish.
  const publish = useMutation({
    mutationFn: async () => {
      await save.mutateAsync(); // flush pending edits first (throws on conflict)
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

  if (site.isLoading || !ready) return <CenteredSpinner label={t('common.loading')} />;

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
          {save.isPending || dirty ? t('common.saving') : status}
        </span>
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
        <Button onClick={() => publish.mutate()} disabled={publish.isPending || !!conflict || switching}>
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
            onClick={() => loadBranch.mutate(branch)}
            disabled={loadBranch.isPending}
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
          onReload={() => loadBranch.mutate(branch)}
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
        dirty={save.isPending || dirty}
        onOpenScm={() => setSidebarView('scm')}
      />
    </div>
  );
}
