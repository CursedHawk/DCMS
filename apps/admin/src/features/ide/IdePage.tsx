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
import { DiffEditor } from './DiffEditor';
import { EditorTabs } from './EditorTabs';
import { ideApi } from './ide';
import { IdeSidebar, type SidebarView } from './IdeSidebar';
import { MonacoEditor } from './MonacoEditor';
import { PreviewPane } from './PreviewPane';
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
  const activePath = useVfs((s) => s.activePath);
  const activeDiff = useVfs((s) => s.activeDiff);
  const openDiffs = useVfs((s) => s.openDiffs);
  const conflict = useVfs((s) => s.conflict);
  const [showPreview, setShowPreview] = useState(true);
  const [sidebarView, setSidebarView] = useState<SidebarView>('files');
  const [status, setStatus] = useState('');
  const [ready, setReady] = useState(false);
  const [previewNonce, setPreviewNonce] = useState(0);
  const loadedFor = useRef<string | null>(null);
  const activeDiffTab = activeDiff ? openDiffs.find((d) => d.path === activeDiff) : undefined;

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
      if (Object.keys(data.files).length > 0) {
        useVfs.getState().load(data.files, data.version, data.hashes);
      } else {
        useVfs.getState().seedStarter({ ...STARTER_FILES });
      }
      useVfs.getState().setBranch(data.branch);
      setReady(true);
    },
    onError: () => toast.error(t('errors.loadFailed')),
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

  // Publish = commit the current draft to the `release` branch → build + deploy.
  const publish = useMutation({
    mutationFn: async () => {
      await save.mutateAsync(); // flush pending edits first (throws on conflict)
      return api.post(`/admin/sites/${siteId}/publish?branch=${encodeURIComponent(branch)}`);
    },
    onSuccess: () => toast.success(t('editor.publishQueued')),
    onError: (e) => {
      if (!(e instanceof ApiError && e.status === 409)) toast.error(t('errors.generic'));
    },
  });

  if (site.isLoading || !ready) return <CenteredSpinner label={t('common.loading')} />;

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
        <Button onClick={() => publish.mutate()} disabled={publish.isPending || !!conflict}>
          <Rocket className="h-4 w-4" /> {t('ide.git.publishToRelease')}
        </Button>
      </div>

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

      {/* Workspace: sidebar (explorer / source control) | editor | preview */}
      <div className="relative flex min-h-0 flex-1">
        <IdeSidebar
          siteId={siteId}
          branch={branch}
          view={sidebarView}
          onViewChange={setSidebarView}
          onSwitchBranch={(b) => loadBranch.mutate(b)}
          onReload={() => loadBranch.mutate(branch)}
          onRestored={(files, version, hashes) => useVfs.getState().load(files, version, hashes)}
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
          <div className="w-1/2 shrink-0 border-l">
            <PreviewPane enabled={showPreview} siteId={siteId} refreshKey={previewNonce} />
          </div>
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
