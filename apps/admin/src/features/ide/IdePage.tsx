import { useMutation, useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { ArrowLeft, Eye, EyeOff, Rocket, RotateCcw } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '../../components/ui/button';
import { CenteredSpinner } from '../../components/ui/spinner';
import { api } from '../../lib/api';
import { isBinaryPath } from './binary';
import { BinaryFileView } from './BinaryFileView';
import { EditorTabs } from './EditorTabs';
import { FileTree } from './FileTree';
import { MonacoEditor } from './MonacoEditor';
import { PreviewPane } from './PreviewPane';
import { STARTER_FILES } from './starter';
import { filesFromDefinition, useVfs } from './vfs';

const sitesPath: string = '/sites';

// The code IDE surface for Mode B (ReactApp) sites. Reads/writes the file map
// via the same site definition endpoints the canvas editor uses.
export function IdePage({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const dirty = useVfs((s) => s.dirty);
  const rev = useVfs((s) => s.rev);
  const activePath = useVfs((s) => s.activePath);
  const load = useVfs((s) => s.load);
  const markSaved = useVfs((s) => s.markSaved);
  const [showPreview, setShowPreview] = useState(true);
  const [status, setStatus] = useState('');
  const [previewNonce, setPreviewNonce] = useState(0);
  const loadedFor = useRef<string | null>(null);

  const site = useQuery({
    queryKey: ['site', siteId],
    queryFn: () => api.get<{ name: string; definition: unknown }>(`/admin/sites/${siteId}`),
  });

  // Hydrate the vfs once per site (seed a starter when the map is empty).
  useEffect(() => {
    if (site.data && loadedFor.current !== siteId) {
      loadedFor.current = siteId;
      const files = filesFromDefinition(site.data.definition);
      load(Object.keys(files).length > 0 ? files : { ...STARTER_FILES });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [site.data, siteId]);

  const save = useMutation({
    mutationFn: () =>
      api.put(`/admin/sites/${siteId}/definition`, { files: useVfs.getState().snapshot() }),
    onSuccess: () => {
      markSaved();
      setStatus(t('common.saved'));
    },
  });

  // Debounced autosave on any content change.
  useEffect(() => {
    if (!dirty || loadedFor.current !== siteId) return;
    const id = setTimeout(() => save.mutate(), 1200);
    return () => clearTimeout(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rev, dirty]);

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

  const publish = useMutation({
    mutationFn: async () => {
      await api.put(`/admin/sites/${siteId}/definition`, { files: useVfs.getState().snapshot() });
      markSaved();
      return api.post<{ buildId: string }>(`/admin/sites/${siteId}/publish`);
    },
    onSuccess: () => toast.success(t('editor.publishQueued')),
    onError: () => toast.error(t('errors.generic')),
  });

  if (site.isLoading) return <CenteredSpinner label={t('common.loading')} />;

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
        <Button onClick={() => publish.mutate()} disabled={publish.isPending}>
          <Rocket className="h-4 w-4" /> {t('actions.publish')}
        </Button>
      </div>

      {/* Workspace: explorer | editor | preview */}
      <div className="flex min-h-0 flex-1">
        <aside className="w-56 shrink-0 border-r bg-card">
          <FileTree />
        </aside>
        <div className="flex min-w-0 flex-1 flex-col">
          <EditorTabs />
          <div className="relative min-h-0 flex-1">
            <MonacoEditor key={siteId} />
            {activePath && isBinaryPath(activePath) && (
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
    </div>
  );
}
