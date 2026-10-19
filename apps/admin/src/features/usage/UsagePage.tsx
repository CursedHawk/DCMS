import { HardDrive, RefreshCw } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { ApiError, bytes, ofLimit, relativeTime } from '@dcms/core';
import {
  Button,
  Card,
  CardContent,
  CenteredSpinner,
  EmptyState,
  LoadFailed,
  Meter,
  Page,
  PageHeader,
  UsageBreakdown,
  toastApiError,
} from '@dcms/ui';
import { useRecalculateUsage, useUsage } from './api';

/**
 * Settings → Usage: everything this workspace stores, largest first.
 *
 * <p>Report only. The storage limit beside it counts media files, as uploads enforce it; the
 * breakdown is what everything else costs, so an admin can see why the workspace is the size
 * it is.</p>
 */
export function UsagePage() {
  const { t, i18n } = useTranslation();
  const usage = useUsage();
  const recalculate = useRecalculateUsage();

  const measure = () =>
    recalculate.mutate(undefined, {
      onSuccess: () => toast.success(t('usage.recalculated')),
      onError: (e) =>
        e instanceof ApiError && e.status === 429 ? toast.info(t('usage.tooSoon')) : toastApiError(e, t),
    });

  const measureButton = (
    <Button variant="outline" onClick={measure} disabled={recalculate.isPending}>
      <RefreshCw className={recalculate.isPending ? 'animate-spin motion-reduce:animate-none' : undefined} />
      {t('usage.recalculate')}
    </Button>
  );

  const data = usage.data;

  return (
    <Page>
      <PageHeader title={t('usage.title')} description={t('usage.description')} actions={measureButton} />
      {usage.isLoading ? <CenteredSpinner /> : null}
      {usage.isError ? <LoadFailed onRetry={() => void usage.refetch()} /> : null}

      {data && (
        <Card className="mb-6">
          <CardContent className="grid grid-cols-1 gap-6 pt-6 sm:grid-cols-2">
            <div>
              <p className="text-sm text-muted-foreground">{t('usage.total')}</p>
              <p className="mt-1 text-3xl font-semibold tabular-nums">{bytes(data.totalBytes)}</p>
              <p className="mt-1 text-xs text-muted-foreground">
                {data.measuredAt
                  ? t('usage.measured', { when: relativeTime(data.measuredAt, i18n.language) })
                  : t('usage.never')}
              </p>
            </div>
            <div>
              <Meter
                label={t('usage.limit')}
                value={ofLimit(data.mediaBytes, data.quotaBytes)}
                fraction={data.quotaBytes > 0 ? data.mediaBytes / data.quotaBytes : 0}
              />
              <p className="mt-2 text-xs text-muted-foreground">{t('usage.limitHint')}</p>
            </div>
          </CardContent>
        </Card>
      )}

      {data?.measuredAt ? (
        <UsageBreakdown categories={data.categories} totalBytes={data.totalBytes} />
      ) : data ? (
        <EmptyState icon={HardDrive} title={t('usage.never')} description={t('usage.neverHint')} action={measureButton} />
      ) : null}
    </Page>
  );
}
