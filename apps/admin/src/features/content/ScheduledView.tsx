import { useNavigate } from '@tanstack/react-router';
import { AlertTriangle, CalendarClock, CalendarX } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Badge, Button, type Column, DataTable, EmptyState, PermissionTooltip, useCan } from '@dcms/ui';
import { dateTime } from '@dcms/core';
import { linkTarget } from '../notifications/linkPath';
import { Perm } from '../../lib/permissions';
import { type ScheduledItem, useBulkContentAction, useScheduledContent } from './api';

/**
 * The publishing queue: everything waiting to go live, across every collection.
 *
 * <p>The scheduler has worked since it shipped and its queue has never been visible. The
 * console could tell you that the one item you had open was scheduled; it could not answer
 * "what goes out this week", which is the question the feature exists for. Every other content
 * list is scoped to one plugin instance because that is how the console browses — this one is
 * deliberately not, and it is the only read in the CMS that crosses them.</p>
 *
 * <p>Read-only apart from cancelling. Editing what is queued means editing the item, which is
 * what the row click does — and the title shown is the <b>version that is queued</b>, not the
 * draft being written now, so an author who has kept typing since scheduling still sees the
 * headline that is actually going out.</p>
 */
export function ScheduledView() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queue = useScheduledContent();
  const cancel = useBulkContentAction();
  const canPublish = useCan(Perm.ContentPublish);
  const [cancelling, setCancelling] = useState<string | null>(null);

  const items = queue.data?.items ?? [];

  const cancelOne = (row: ScheduledItem) => {
    setCancelling(row.itemId);
    cancel.mutate(
      { verb: 'cancelSchedule', ids: [row.itemId] },
      {
        onSuccess: (result) => {
          if (result.failed.length > 0) toast.error(t('content.bulk.allFailed', { count: 1 }));
          else toast.success(t('content.scheduledView.cancelled', { title: row.title }));
        },
        onSettled: () => setCancelling(null),
      },
    );
  };

  const columns: Column<ScheduledItem>[] = [
    {
      id: 'when',
      header: t('content.scheduledView.when'),
      primary: true,
      cell: (row) => (
        <span className="whitespace-nowrap font-medium tabular-nums">{dateTime(row.publishAt)}</span>
      ),
      sortValue: (row) => row.publishAt,
    },
    {
      id: 'title',
      header: t('content.itemTitle'),
      cell: (row) => (
        <>
          <span>{row.title}</span>
          {/* The item may already be live: a queued publish on a published item is the next
              revision, not its first appearance, and those are different pieces of news. */}
          {row.status === 'Published' ? (
            <Badge tone="success" className="ml-2 text-[10px]">
              {t('content.scheduledView.revision')}
            </Badge>
          ) : null}
        </>
      ),
      sortValue: (row) => row.title.toLowerCase(),
    },
    {
      id: 'where',
      header: t('content.scheduledView.collection'),
      cell: (row) => (
        <span className="text-muted-foreground">
          {row.instanceName} · {row.contentType}
        </span>
      ),
      sortValue: (row) => row.instanceName.toLowerCase(),
    },
    {
      id: 'cancel',
      header: '',
      srHeader: t('content.scheduledView.cancel'),
      align: 'right',
      width: '1%',
      cell: (row) => (
        <PermissionTooltip perm={Perm.ContentPublish} reason={t('content.bulk.needsPublish')}>
          <Button
            size="sm"
            variant="ghost"
            disabled={!canPublish || cancelling === row.itemId}
            // The row itself opens the item; this must not do both.
            onClick={(e) => {
              e.stopPropagation();
              cancelOne(row);
            }}
          >
            <CalendarX className="h-4 w-4" /> {t('content.scheduledView.cancel')}
          </Button>
        </PermissionTooltip>
      ),
    },
  ];

  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-lg font-semibold tracking-tight">{t('content.scheduledView.title')}</h2>
        <p className="text-sm text-muted-foreground">{t('content.scheduledView.subtitle')}</p>
      </div>

      <DataTable
        rows={items}
        columns={columns}
        rowKey={(row) => row.scheduleId}
        isLoading={queue.isLoading}
        defaultSort={{ columnId: 'when', direction: 'asc' }}
        caption={t('content.scheduledView.title')}
        labels={{
          loading: t('common.loading'),
          sortBy: (column) => t('common.sortBy', { column }),
        }}
        /*
         * Opens the item in its own collection, which is where editing a scheduled item belongs.
         * Through `linkTarget` because this app builds its routes with a helper, so the router's
         * generated union of valid `to` values holds only the three routes declared literally —
         * the cast and the reason for it live in one place rather than at every call site.
         */
        onRowClick={(row) =>
          void navigate(
            linkTarget(
              `/content?instance=${row.instanceId}&type=${encodeURIComponent(row.contentType)}` +
                `&item=${row.itemId}`,
            ),
          )
        }
        error={
          queue.isError ? (
            <EmptyState
              icon={AlertTriangle}
              title={t('common.loadFailed')}
              action={
                <Button variant="outline" size="sm" onClick={() => void queue.refetch()}>
                  {t('common.retry')}
                </Button>
              }
            />
          ) : undefined
        }
        empty={
          <EmptyState
            icon={CalendarClock}
            title={t('content.scheduledView.emptyTitle')}
            description={t('content.scheduledView.emptyHint')}
          />
        }
      />
    </div>
  );
}
