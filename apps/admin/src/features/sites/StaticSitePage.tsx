import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import {
  ArrowLeft,
  CheckCircle2,
  FileArchive,
  FolderUp,
  RotateCcw,
  Rocket,
  Upload,
} from 'lucide-react';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Card, CardContent } from '../../components/ui/card';
import { ResourceHistory } from '../audit/ResourceHistory';
import { Progress } from '../../components/ui/progress';
import { CenteredSpinner } from '../../components/ui/spinner';
import { cn } from '../../lib/cn';
import { api } from '../../lib/api';
import { toastApiError } from '../../lib/errors';

interface StaticBundle {
  name?: string;
  size?: number;
  fileCount?: number;
  uploadedAt?: string;
}
interface SiteDetail {
  id: string;
  name: string;
  renderMode: string;
  activeBuildId?: string;
  staticBundle?: StaticBundle | null;
}
interface SiteBuild {
  id: string;
  status: string;
  error?: string;
  createdAt: string;
  completedAt?: string;
  isActive: boolean;
}

const sitesPath: string = '/sites';

function formatBytes(n?: number): string {
  if (!n) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  const i = Math.min(units.length - 1, Math.floor(Math.log(n) / Math.log(1024)));
  return `${(n / 1024 ** i).toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

export function StaticSitePage({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const zipInput = useRef<HTMLInputElement>(null);
  const folderInput = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);
  // Fraction of the bundle sent so far. A site upload is a whole built website —
  // often tens of megabytes — so a bare "processing…" reads as a hang.
  const [uploadProgress, setUploadProgress] = useState(0);

  const site = useQuery({
    queryKey: ['site', siteId],
    queryFn: () => api.get<SiteDetail>(`/admin/sites/${siteId}`),
  });
  const builds = useQuery({
    queryKey: ['site-builds', siteId],
    queryFn: () => api.get<SiteBuild[]>(`/admin/sites/${siteId}/builds`),
  });

  const upload = useMutation({
    mutationFn: (files: FileList | File[]) => {
      const fd = new FormData();
      for (const file of Array.from(files)) {
        const path = (file as File & { webkitRelativePath?: string }).webkitRelativePath;
        fd.append('files', file, path && path.length > 0 ? path : file.name);
      }
      setUploadProgress(0);
      return api.uploadWithProgress<{ fileCount: number; size: number; name: string; hasIndex: boolean }>(
        `/admin/sites/${siteId}/upload`,
        fd,
        setUploadProgress,
      );
    },
    onSuccess: async (r) => {
      if (!r.hasIndex) toast.warning(t('sites.noIndexWarning'));
      else toast.success(t('sites.uploadDone', { count: r.fileCount }));
      await qc.invalidateQueries({ queryKey: ['site', siteId] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const publish = useMutation({
    mutationFn: () => api.post(`/admin/sites/${siteId}/publish`),
    onSuccess: async () => {
      toast.success(t('editor.publishQueued'));
      await qc.invalidateQueries({ queryKey: ['site-builds', siteId] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const activate = useMutation({
    mutationFn: (buildId: string) => api.post(`/admin/sites/${siteId}/builds/${buildId}/activate`),
    onSuccess: async () => {
      toast.success(t('sites.activated'));
      await Promise.all([
        qc.invalidateQueries({ queryKey: ['site-builds', siteId] }),
        qc.invalidateQueries({ queryKey: ['site', siteId] }),
      ]);
    },
    onError: (e) => toastApiError(e, t),
  });

  if (site.isLoading) return <CenteredSpinner label={t('common.loading')} />;

  const bundle = site.data?.staticBundle;

  return (
    <div className="mx-auto max-w-3xl p-6">
      <div className="mb-6 flex items-center gap-3">
        <Link to={sitesPath}>
          <Button size="icon" variant="ghost">
            <ArrowLeft className="h-4 w-4" />
          </Button>
        </Link>
        <div className="flex-1">
          <h1 className="text-lg font-semibold">{site.data?.name}</h1>
          <p className="text-xs text-muted-foreground">{t('sites.modeStaticFiles')}</p>
        </div>
        <Button
          onClick={() => publish.mutate()}
          disabled={!bundle || publish.isPending}
          title={!bundle ? t('sites.uploadFirst') : undefined}
        >
          <Rocket className="h-4 w-4" /> {t('actions.publish')}
        </Button>
      </div>

      {/* Upload zone */}
      <Card>
        <CardContent className="p-5">
          {/* eslint-disable-next-line jsx-a11y/no-static-element-interactions */}
          <div
            onDragOver={(e) => {
              e.preventDefault();
              setDragging(true);
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={(e) => {
              e.preventDefault();
              setDragging(false);
              if (e.dataTransfer.files.length) upload.mutate(e.dataTransfer.files);
            }}
            className={cn(
              'flex flex-col items-center justify-center gap-3 rounded-lg border-2 border-dashed p-8 text-center transition-colors',
              dragging ? 'border-primary bg-primary/5' : 'border-input',
            )}
          >
            <Upload className="h-7 w-7 text-muted-foreground" />
            <p className="text-sm text-muted-foreground">{t('sites.dropHint')}</p>
            <div className="flex flex-wrap justify-center gap-2">
              <Button variant="outline" size="sm" disabled={upload.isPending} onClick={() => zipInput.current?.click()}>
                <FileArchive className="h-4 w-4" /> {t('sites.chooseZip')}
              </Button>
              <Button variant="outline" size="sm" disabled={upload.isPending} onClick={() => folderInput.current?.click()}>
                <FolderUp className="h-4 w-4" /> {t('sites.chooseFolder')}
              </Button>
            </div>
            {upload.isPending ? (
              <div className="w-full max-w-sm space-y-1">
                <Progress value={uploadProgress} label={t('sites.chooseZip')} />
                <p className="text-xs text-muted-foreground">
                  {/* Once the bytes are gone the server is still unpacking, so the
                      last step reads as "processing" rather than a stalled 100%. */}
                  {uploadProgress < 1
                    ? `${Math.round(uploadProgress * 100)}%`
                    : `${t('media.processing')}…`}
                </p>
              </div>
            ) : null}
            <input
              ref={zipInput}
              type="file"
              accept=".zip,application/zip"
              className="hidden"
              onChange={(e) => {
                if (e.target.files?.length) upload.mutate(e.target.files);
                e.target.value = '';
              }}
            />
            <input
              ref={folderInput}
              type="file"
              multiple
              // webkitdirectory is non-standard; supplied via spread to satisfy TS.
              {...{ webkitdirectory: '', directory: '' }}
              className="hidden"
              onChange={(e) => {
                if (e.target.files?.length) upload.mutate(e.target.files);
                e.target.value = '';
              }}
            />
          </div>

          {bundle ? (
            <div className="mt-4 flex items-center gap-3 rounded-md border bg-muted/30 p-3 text-sm">
              <CheckCircle2 className="h-4 w-4 text-emerald-500" />
              <div className="min-w-0 flex-1">
                <p className="truncate font-medium">{bundle.name}</p>
                <p className="text-xs text-muted-foreground">
                  {t('sites.bundleSummary', {
                    count: bundle.fileCount ?? 0,
                    size: formatBytes(bundle.size),
                  })}
                </p>
              </div>
              <Badge tone="secondary">{t('sites.staged')}</Badge>
            </div>
          ) : null}
        </CardContent>
      </Card>

      {/* Build history / rollback */}
      <h2 className="mb-2 mt-8 text-sm font-semibold">{t('sites.deployments')}</h2>
      {builds.isLoading ? (
        <CenteredSpinner />
      ) : builds.data && builds.data.length > 0 ? (
        <div className="space-y-2">
          {builds.data.map((b) => (
            <Card key={b.id}>
              <CardContent className="flex items-center gap-3 p-4">
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <BuildStatusBadge status={b.status} t={t} />
                    {b.isActive ? <Badge tone="success">{t('sites.live')}</Badge> : null}
                  </div>
                  <p className="mt-1 text-xs text-muted-foreground">
                    {new Date(b.createdAt).toLocaleString()}
                  </p>
                  {b.error ? <p className="mt-1 text-xs text-destructive">{b.error}</p> : null}
                </div>
                {!b.isActive && b.status === 'Succeeded' ? (
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={activate.isPending}
                    onClick={() => activate.mutate(b.id)}
                  >
                    <RotateCcw className="h-4 w-4" /> {t('sites.rollback')}
                  </Button>
                ) : null}
              </CardContent>
            </Card>
          ))}
        </div>
      ) : (
        <p className="py-6 text-center text-sm text-muted-foreground">{t('sites.noDeployments')}</p>
      )}

      {/* Who changed this site, and when. The audit page can answer the same question, but
          only for someone who already knows to go there and what to filter by. */}
      <h2 className="mb-2 mt-8 text-sm font-semibold">{t('audit.history.title')}</h2>
      <ResourceHistory resourceType="site" resourceId={siteId} />
    </div>
  );
}

function BuildStatusBadge({ status, t }: { status: string; t: (k: string) => string }) {
  const tone =
    status === 'Succeeded'
      ? 'success'
      : status === 'Failed'
        ? 'destructive'
        : status === 'Building'
          ? 'default'
          : 'secondary';
  return <Badge tone={tone as never}>{t(`sites.build${status}`)}</Badge>;
}
