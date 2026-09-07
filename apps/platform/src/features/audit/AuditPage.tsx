import { useInfiniteQuery } from '@tanstack/react-query';
import { Button, CenteredSpinner, EmptyState, Table, TBody, TD, TH, THead, TR } from '@dcms/ui';
import { adminApi } from '../../lib/api';
import { date } from '../../lib/format';

interface AuditItem {
  id: string;
  occurredAt: string;
  action: string;
  category: string;
  outcome: string;
  severity: string;
  actorDisplay: string | null;
  resourceType: string | null;
  resourceLabel: string | null;
  traceId: string | null;
}

interface AuditPageResponse {
  items: AuditItem[];
  hasMore: boolean;
  nextCursor: { occurredAt: string; seq: number } | null;
}

/**
 * The platform's own audit log — the records written with no tenant at all.
 *
 * <p>These have existed since the audit design shipped and until now had no reader: the tenant
 * audit view surfaces a platform record only when it names one of that tenant's own members, so
 * a tenant suspension or a role grant, which name no workspace, were visible to nobody. This
 * page is `?scope=platform`, which admin-api serves to SuperAdmins only.</p>
 */
export function AuditPage() {
  const q = useInfiniteQuery({
    queryKey: ['platform-audit'],
    initialPageParam: null as { occurredAt: string; seq: number } | null,
    queryFn: ({ pageParam }) => {
      const params = new URLSearchParams({ scope: 'platform', limit: '50' });
      if (pageParam) {
        params.set('beforeOccurredAt', pageParam.occurredAt);
        params.set('beforeSeq', String(pageParam.seq));
      }
      return adminApi.get<AuditPageResponse>(`/admin/audit?${params.toString()}`);
    },
    getNextPageParam: (last) => (last.hasMore ? last.nextCursor : undefined),
  });

  const items = q.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <div className="mx-auto w-full max-w-5xl px-6 py-8">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold tracking-tight">Audit log</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          What has been done to the platform itself — tenant suspensions, role changes, sign-ins.
          Records are hash-chained and kept 400 days; nothing on this console can delete one.
        </p>
      </header>

      {q.isLoading ? (
        <CenteredSpinner />
      ) : items.length === 0 ? (
        <EmptyState
          title="Nothing recorded yet"
          description="Platform-scope records appear here as soon as an operator acts."
        />
      ) : (
        <>
          <Table>
            <THead>
              <TR>
                <TH>When</TH>
                <TH>Action</TH>
                <TH>Who</TH>
                <TH>Subject</TH>
                <TH>Trace</TH>
              </TR>
            </THead>
            <TBody>
              {items.map((e) => (
                <TR key={e.id}>
                  <TD className="whitespace-nowrap text-sm text-muted-foreground">{date(e.occurredAt)}</TD>
                  <TD>
                    <span className="font-mono text-xs">{e.action}</span>
                    {e.outcome !== 'success' && (
                      <span className="ml-2 rounded-full bg-[hsl(var(--warning)/0.15)] px-1.5 py-0.5 text-[0.6875rem]">
                        {e.outcome}
                      </span>
                    )}
                  </TD>
                  <TD className="text-sm">{e.actorDisplay ?? '—'}</TD>
                  <TD className="text-sm text-muted-foreground">
                    {e.resourceLabel ?? e.resourceType ?? '—'}
                  </TD>
                  <TD>
                    {e.traceId
                      ? <span className="font-mono text-xs text-muted-foreground">{e.traceId.slice(0, 12)}</span>
                      : <span className="text-muted-foreground">—</span>}
                  </TD>
                </TR>
              ))}
            </TBody>
          </Table>

          {q.hasNextPage && (
            <div className="mt-4">
              <Button
                variant="outline"
                size="sm"
                disabled={q.isFetchingNextPage}
                onClick={() => void q.fetchNextPage()}
              >
                {q.isFetchingNextPage ? 'Loading' : 'Load older records'}
              </Button>
            </div>
          )}
        </>
      )}
    </div>
  );
}
