import { Image as ImageIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Page, PageHeader } from '../../components/Page';
import { Badge } from '../../components/ui/badge';
import { EmptyState } from '../../components/ui/empty-state';
import { CenteredSpinner } from '../../components/ui/spinner';
import { MediaThumb } from './MediaThumb';
import { MediaUploader } from './MediaUploader';
import { type MediaStatus, formatSize, useMedia } from './api';

const statusTone: Record<MediaStatus, 'success' | 'warning' | 'destructive' | 'secondary'> = {
  Ready: 'success',
  Processing: 'warning',
  Uploaded: 'secondary',
  Failed: 'destructive',
};

export function MediaPage() {
  const { t } = useTranslation();
  const media = useMedia();

  return (
    <Page>
      <PageHeader title={t('media.title')} />
      <div className="mb-6">
        <MediaUploader />
      </div>

      {media.isLoading ? (
        <CenteredSpinner />
      ) : media.data && media.data.length > 0 ? (
        <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5">
          {media.data.map((a) => (
            <div key={a.id} className="group overflow-hidden rounded-lg border bg-card">
              <div className="aspect-square overflow-hidden">
                <MediaThumb id={a.id} category={a.category} status={a.status} />
              </div>
              <div className="space-y-1 p-2.5">
                <p className="truncate text-xs font-medium" title={a.fileName}>
                  {a.fileName}
                </p>
                <div className="flex items-center justify-between">
                  <span className="text-[11px] text-muted-foreground">{formatSize(a.sizeBytes)}</span>
                  <Badge tone={statusTone[a.status]} className="text-[10px]">
                    {t(`media.${a.status.toLowerCase()}`, a.status)}
                  </Badge>
                </div>
              </div>
            </div>
          ))}
        </div>
      ) : (
        <EmptyState icon={ImageIcon} title={t('media.title')} description={t('media.dropHere')} />
      )}
    </Page>
  );
}
