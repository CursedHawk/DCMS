import { useInfiniteQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/button';
import { EmptyState } from '../../components/ui/empty-state';
import { CenteredSpinner } from '../../components/ui/spinner';
import { api } from '../../lib/api';
import { Perm, can, useMyPermissions } from '../../lib/permissions';
import { AuditRecordDialog } from './AuditRecordDialog';
import type { AuditPage } from './types';

/**
 * "What happened to this thing" — the audit log narrowed to one resource, embeddable wherever
 * that resource is edited.
 *
 * <p>Most questions arrive attached to something specific: why is this site pointing at the
 * wrong build, who unpublished this page, when did this role stop being able to publish. The
 * central audit page can answer all of them, but only if the person already knows to go there
 * and what to filter by. Putting the same query next to the thing itself is the difference
 * between an audit log that gets used and one that gets built.</p>
 *
 * <p>Renders nothing at all without `audit:read`, rather than an empty state — a tab that is
 * always empty reads as a broken feature, not as a permission the viewer lacks.</p>
 */
export function ResourceHistory({
  resourceType,
  resourceId,
}: {
  resourceType: string;
  resourceId: string;
}) {
  const { t } = useTranslation();
  const me = useMyPermissions(true);
  const canRead = can(me.data, Perm.AuditRead);
  const [openId, setOpenId] = useState<string | null>(null);

  const query = useInfiniteQuery({
    queryKey: ['audit', 'resource', resourceType, resourceId],
    initialPageParam: null as AuditPage['nextCursor'],
    queryFn: ({ pageParam }) => {
      const params = new URLSearchParams({ resourceType, resourceId, limit: '25' });
      if (pageParam) {
        params.set('beforeOccurredAt', pageParam.occurredAt);
        params.set('beforeSeq', String(pageParam.seq));
      }
      return api.get<AuditPage>(`/admin/audit?${params}`);
    },
    getNextPageParam: (last) => (last.hasMore ? last.nextCursor : undefined),
    enabled: canRead,
  });

  if (!canRead) return null;
  if (query.isLoading) return <CenteredSpinner />;

  const records = query.data?.pages.flatMap((p) => p.items) ?? [];
  if (records.length === 0) {
    return <EmptyState title={t('audit.history.empty')} description={t('audit.history.emptyHint')} />;
  }

  return (
    <>
      <ul className="divide-y rounded-md border">
        {records.map((r) => (
          <li key={r.id}>
            <button
              type="button"
              className="flex w-full items-baseline gap-3 px-4 py-2.5 text-left text-sm hover:bg-muted/50"
              onClick={() => setOpenId(r.id)}
            >
              <span className="w-40 shrink-0 tabular-nums text-xs text-muted-foreground">
                {new Date(r.occurredAt).toLocaleString()}
              </span>
              <span className="min-w-0 flex-1">
                <span className="font-medium">
                  {t(`audit.action.${r.action}`, { defaultValue: r.action })}
                </span>
                {r.outcome !== 'success' ? (
                  <span className="ml-2 text-xs text-destructive">
                    {t(`audit.outcome.${r.outcome}`, { defaultValue: r.outcome })}
                  </span>
                ) : null}
              </span>
              <span className="shrink-0 text-xs text-muted-foreground">
                {r.actor.display ?? r.actor.ref ?? t('audit.actorUnknown')}
              </span>
            </button>
          </li>
        ))}
      </ul>

      {query.hasNextPage ? (
        <div className="mt-3 flex justify-center">
          <Button
            variant="outline"
            size="sm"
            onClick={() => void query.fetchNextPage()}
            disabled={query.isFetchingNextPage}
          >
            {t('audit.loadMore')}
          </Button>
        </div>
      ) : null}

      <AuditRecordDialog id={openId} onClose={() => setOpenId(null)} />
    </>
  );
}
