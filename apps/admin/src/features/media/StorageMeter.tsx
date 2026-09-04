import { Film, HardDrive, Image as ImageIcon, Music, File as FileIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { cn, Skeleton } from '@dcms/admin-ui';
import { type MediaCategory, type MediaUsage, formatSize } from './api';

/** Colour + icon per media category, shared by the meter and the grid. */
export const categoryMeta: Record<MediaCategory, { color: string; icon: typeof ImageIcon }> = {
  Image: { color: 'bg-sky-500', icon: ImageIcon },
  Video: { color: 'bg-violet-500', icon: Film },
  Audio: { color: 'bg-emerald-500', icon: Music },
  File: { color: 'bg-amber-500', icon: FileIcon },
};

const order: MediaCategory[] = ['Image', 'Video', 'Audio', 'File'];

/**
 * Storage-footprint panel: total bytes the tenant occupies, a segmented bar by
 * category, and the originals-vs-optimized split so the cost of derived
 * renditions is visible.
 */
export function StorageMeter({ usage, isLoading }: { usage?: MediaUsage; isLoading?: boolean }) {
  const { t } = useTranslation();

  if (isLoading || !usage) {
    return (
      <div className="rounded-xl border bg-card p-4">
        <Skeleton className="h-5 w-40" />
        <Skeleton className="mt-3 h-2.5 w-full rounded-full" />
        <Skeleton className="mt-3 h-4 w-64" />
      </div>
    );
  }

  const byCat = new Map(usage.byCategory.map((c) => [c.category, c]));
  const totalOriginal = usage.originalBytes || 1; // avoid /0 for the bar

  return (
    <div className="rounded-xl border bg-card p-4">
      <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <div className="flex items-center gap-2">
          <HardDrive className="h-4 w-4 text-muted-foreground" />
          <h2 className="text-sm font-semibold">{t('media.usage.title')}</h2>
        </div>
        <div className="flex items-baseline gap-1.5">
          <span className="text-2xl font-bold tabular-nums">{formatSize(usage.totalBytes)}</span>
          <span className="text-xs text-muted-foreground">{t('media.usage.used')}</span>
        </div>
      </div>

      {/* Segmented bar: each category's share of the originals footprint. */}
      <div className="mt-3 flex h-2.5 w-full overflow-hidden rounded-full bg-muted">
        {order.map((cat) => {
          const bytes = byCat.get(cat)?.originalBytes ?? 0;
          if (bytes === 0) return null;
          return (
            <div
              key={cat}
              className={cn('h-full', categoryMeta[cat].color)}
              style={{ width: `${(bytes / totalOriginal) * 100}%` }}
              title={`${cat}: ${formatSize(bytes)}`}
            />
          );
        })}
      </div>

      <div className="mt-3 flex flex-wrap items-center gap-x-5 gap-y-2 text-xs">
        {order.map((cat) => {
          const c = byCat.get(cat);
          if (!c || c.count === 0) return null;
          return (
            <span key={cat} className="flex items-center gap-1.5 text-muted-foreground">
              <span className={cn('h-2.5 w-2.5 rounded-sm', categoryMeta[cat].color)} />
              <span className="font-medium text-foreground">{t(`media.category.${cat}`)}</span>
              {c.count} · {formatSize(c.originalBytes)}
            </span>
          );
        })}
      </div>

      <div className="mt-3 flex flex-wrap gap-x-6 gap-y-1 border-t pt-3 text-xs text-muted-foreground">
        <span>
          {t('media.usage.originals')}:{' '}
          <span className="font-medium text-foreground">{formatSize(usage.originalBytes)}</span>
        </span>
        <span>
          {t('media.usage.optimized')}:{' '}
          <span className="font-medium text-foreground">{formatSize(usage.variantBytes)}</span>
        </span>
        <span>
          {t('media.usage.assets', { count: usage.assetCount })}
        </span>
        <span>
          {t('media.usage.folders', { count: usage.folderCount })}
        </span>
      </div>
    </div>
  );
}
