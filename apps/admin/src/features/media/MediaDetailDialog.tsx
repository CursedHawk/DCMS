import { Download, ExternalLink, FolderInput, Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { AuthedImage } from '../../components/AuthedImage';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '../../components/ui/dialog';
import { Input } from '../../components/ui/input';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '../../components/ui/select';
import { CenteredSpinner } from '../../components/ui/spinner';
import { fetchObjectUrl, mediaContentPath } from '../../lib/api';
import { cn } from '../../lib/cn';
import { MediaThumb } from './MediaThumb';
import {
  type MediaFolder,
  formatDate,
  formatSize,
  useDeleteAssets,
  useMediaDetail,
  useMoveAssets,
  useRenameAsset,
} from './api';

/**
 * Full detail for one asset: a large preview, editable name, folder placement,
 * upload date, and — the point of it — the full-vs-optimized rendition breakdown
 * so the tenant can see the original next to every derived (compressed) variant.
 */
export function MediaDetailDialog({
  assetId,
  folders,
  onOpenChange,
  onDeleted,
}: {
  assetId: string | null;
  folders: MediaFolder[];
  onOpenChange: (open: boolean) => void;
  onDeleted?: () => void;
}) {
  const { t } = useTranslation();
  const detail = useMediaDetail(assetId ?? undefined);
  const rename = useRenameAsset();
  const move = useMoveAssets();
  const del = useDeleteAssets();

  const [name, setName] = useState('');
  useEffect(() => {
    if (detail.data) setName(detail.data.fileName);
  }, [detail.data]);

  const a = detail.data;

  const saveName = () => {
    if (!a || !name.trim() || name.trim() === a.fileName) return;
    rename.mutate({ id: a.id, fileName: name.trim() }, { onError: () => toast.error(t('errors.generic')) });
  };

  const changeFolder = (folderId: string) => {
    if (!a) return;
    move.mutate(
      { ids: [a.id], folderId: folderId === 'none' ? null : folderId },
      { onError: () => toast.error(t('errors.generic')) },
    );
  };

  const remove = () => {
    if (!a || !window.confirm(t('media.deleteConfirm', { name: a.fileName }))) return;
    del.mutate([a.id], {
      onSuccess: () => {
        onDeleted?.();
        onOpenChange(false);
      },
      onError: () => toast.error(t('errors.generic')),
    });
  };

  // Open a rendition (original when `variant` is undefined) in a new browser tab.
  // The bytes are bearer-gated, so we fetch a blob: URL rather than linking the
  // raw content path — a plain <a>/window.open couldn't send the auth header.
  const openVariant = async (variant?: string) => {
    if (!a) return;
    try {
      const url = await fetchObjectUrl(mediaContentPath(a.id, variant));
      window.open(url, '_blank', 'noopener');
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch {
      toast.error(t('errors.generic'));
    }
  };

  const downloadVariant = async (variant: string | undefined, fileName: string) => {
    if (!a) return;
    try {
      const url = await fetchObjectUrl(mediaContentPath(a.id, variant));
      const link = document.createElement('a');
      link.href = url;
      link.download = fileName;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    } catch {
      toast.error(t('errors.generic'));
    }
  };

  return (
    <Dialog open={assetId !== null} onOpenChange={onOpenChange}>
      <DialogContent wide className="max-w-3xl">
        <DialogHeader>
          <DialogTitle className="truncate">{a?.fileName ?? t('media.detail.title')}</DialogTitle>
        </DialogHeader>

        {!a ? (
          <CenteredSpinner />
        ) : (
          <DialogBody className="space-y-5">
            <div className="grid gap-5 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
              {/* Preview */}
              <div className="overflow-hidden rounded-lg border bg-muted">
                <div className="flex aspect-video items-center justify-center">
                  {a.category === 'Image' && a.status === 'Ready' ? (
                    <AuthedImage id={a.id} className="max-h-full max-w-full object-contain" />
                  ) : (
                    <MediaThumb id={a.id} category={a.category} status={a.status} className="h-full w-full" />
                  )}
                </div>
              </div>

              {/* Metadata */}
              <div className="space-y-3 text-sm">
                <div className="space-y-1">
                  <label className="text-xs font-medium text-muted-foreground">{t('media.detail.name')}</label>
                  <div className="flex gap-2">
                    <Input value={name} onChange={(e) => setName(e.target.value)} onBlur={saveName} className="h-8" />
                  </div>
                </div>

                <div className="space-y-1">
                  <label className="text-xs font-medium text-muted-foreground">{t('media.detail.folder')}</label>
                  <Select value={a.folderId ?? 'none'} onValueChange={changeFolder}>
                    <SelectTrigger className="h-8">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="none">{t('media.folders.unfiled')}</SelectItem>
                      {folders.map((f) => (
                        <SelectItem key={f.id} value={f.id}>
                          {f.name}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </div>

                <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 pt-1 text-xs">
                  <dt className="text-muted-foreground">{t('media.detail.type')}</dt>
                  <dd className="font-medium">
                    <Badge tone="secondary">{t(`media.category.${a.category}`)}</Badge>{' '}
                    <span className="text-muted-foreground">{a.contentType}</span>
                  </dd>
                  <dt className="text-muted-foreground">{t('media.detail.status')}</dt>
                  <dd className="font-medium">{t(`media.${a.status.toLowerCase()}`, a.status)}</dd>
                  <dt className="text-muted-foreground">{t('media.detail.uploaded')}</dt>
                  <dd className="font-medium">{formatDate(a.createdAt)}</dd>
                </dl>

                <div className="flex flex-wrap gap-2 pt-1">
                  <Button size="sm" variant="outline" onClick={() => downloadVariant(undefined, a.fileName)}>
                    <Download className="h-4 w-4" /> {t('media.detail.download')}
                  </Button>
                  <Button size="sm" variant="destructive" onClick={remove} disabled={del.isPending}>
                    <Trash2 className="h-4 w-4" /> {t('common.delete')}
                  </Button>
                </div>
              </div>
            </div>

            {/* Full vs. optimized renditions */}
            <div>
              <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                {t('media.detail.versions')}
              </h3>
              <div className="overflow-hidden rounded-lg border">
                <table className="w-full text-sm">
                  <thead className="bg-muted/50 text-xs text-muted-foreground">
                    <tr>
                      <th className="px-3 py-2 text-left font-medium">{t('media.detail.version')}</th>
                      <th className="px-3 py-2 text-left font-medium">{t('media.detail.dimensions')}</th>
                      <th className="px-3 py-2 text-right font-medium">{t('media.detail.fileSize')}</th>
                      <th className="px-3 py-2 text-right font-medium">{t('media.detail.vsOriginal')}</th>
                      <th className="w-px px-3 py-2" />
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    <tr>
                      <td className="px-3 py-2">
                        <span className="font-medium">{t('media.detail.original')}</span>
                        <span className="ml-1.5 text-xs text-muted-foreground">{t('media.detail.full')}</span>
                      </td>
                      <td className="px-3 py-2 text-muted-foreground">—</td>
                      <td className="px-3 py-2 text-right tabular-nums">{formatSize(a.sizeBytes)}</td>
                      <td className="px-3 py-2 text-right text-muted-foreground">100%</td>
                      <td className="px-3 py-2">
                        <RowActions
                          onOpen={() => openVariant()}
                          onDownload={() => downloadVariant(undefined, a.fileName)}
                        />
                      </td>
                    </tr>
                    {a.variants.map((v) => {
                      const pct = a.sizeBytes > 0 ? Math.round((v.sizeBytes / a.sizeBytes) * 100) : 0;
                      return (
                        <tr key={v.kind}>
                          <td className="px-3 py-2">
                            <span className="font-medium">{v.kind}</span>
                            <span className="ml-1.5 text-xs text-muted-foreground">{t('media.detail.optimized')}</span>
                          </td>
                          <td className="px-3 py-2 text-muted-foreground">
                            {v.width && v.height ? `${v.width}×${v.height}` : '—'}
                          </td>
                          <td className="px-3 py-2 text-right tabular-nums">{formatSize(v.sizeBytes)}</td>
                          <td
                            className={cn(
                              'px-3 py-2 text-right tabular-nums',
                              pct < 100 ? 'text-emerald-600 dark:text-emerald-400' : 'text-muted-foreground',
                            )}
                          >
                            {pct < 100 ? `−${100 - pct}%` : `${pct}%`}
                          </td>
                          <td className="px-3 py-2">
                            <RowActions
                              onOpen={() => openVariant(v.kind)}
                              onDownload={() =>
                                downloadVariant(v.kind, variantFileName(a.fileName, v.kind, v.contentType))
                              }
                            />
                          </td>
                        </tr>
                      );
                    })}
                    {a.variants.length === 0 ? (
                      <tr>
                        <td colSpan={5} className="px-3 py-3 text-center text-xs text-muted-foreground">
                          {a.status === 'Processing' || a.status === 'Uploaded'
                            ? t('media.detail.processing')
                            : t('media.detail.noVariants')}
                        </td>
                      </tr>
                    ) : null}
                  </tbody>
                </table>
              </div>
              <p className="mt-1.5 flex items-center gap-1 text-xs text-muted-foreground">
                <FolderInput className="h-3 w-3" />
                {t('media.detail.versionsHint')}
              </p>
            </div>
          </DialogBody>
        )}
      </DialogContent>
    </Dialog>
  );
}

/** Open-in-new-tab + download icon buttons for one rendition row. */
function RowActions({ onOpen, onDownload }: { onOpen: () => void; onDownload: () => void }) {
  const { t } = useTranslation();
  return (
    <div className="flex justify-end gap-0.5">
      <Button
        size="icon"
        variant="ghost"
        className="h-7 w-7"
        onClick={onOpen}
        title={t('media.detail.openTitle')}
        aria-label={t('media.detail.openTitle')}
      >
        <ExternalLink className="h-4 w-4" />
      </Button>
      <Button
        size="icon"
        variant="ghost"
        className="h-7 w-7"
        onClick={onDownload}
        title={t('media.detail.downloadTitle')}
        aria-label={t('media.detail.downloadTitle')}
      >
        <Download className="h-4 w-4" />
      </Button>
    </div>
  );
}

/** Suggests a download name for a variant, e.g. "photo.jpg" + "webp-640" → "photo-webp-640.webp". */
function variantFileName(base: string, kind: string, contentType: string): string {
  const dot = base.lastIndexOf('.');
  const stem = dot > 0 ? base.slice(0, dot) : base;
  const ext = contentType.split('/')[1]?.split(';')[0] || 'bin';
  return `${stem}-${kind}.${ext}`;
}
