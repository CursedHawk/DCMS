import { useInfiniteQuery } from '@tanstack/react-query';
import { Button, type Column, DataTable, EmptyState } from '@dcms/ui';
import { platformApi } from '../../lib/api';
import { date } from '../../lib/format';

interface AuditItem {
  id: string;
  occurredAt: string;
  action: string;
  category: string;
  outcome: string;
  severity: number;
  actorKind: string;
  actorDisplay: string | null;
  /** direct | propagated | inferred — see AuditAttribution. */
  actorAttribution: string;
  resourceType: string | null;
  resourceLabel: string | null;
  service: string | null;
  statusCode: number | null;
  traceId: string | null;
  correlationId: string | null;
  seq: number;
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
 * a tenant suspension or a role grant, which name no workspace, were visible to nobody.</p>
 *
 * <p>Read from `obs.v_audit_recent` through platform-api rather than from admin-api. That view
 * is the console's read model and already carried what this page renders — which is how the
 * page shipped asking admin-api for `actorDisplay` and `traceId` while admin-api projected
 * `actor.display` and `correlationId`, leaving Who and Trace empty on every row. Writing here
 * remains impossible by construction: the console's database role has no grant on the audit
 * schema at all.</p>
 */
/**
 * The columns, at module scope: this console ships one language and nothing here closes over
 * component state, so rebuilding them per render would be work for nothing.
 */
const columns: Column<AuditItem>[] = [
  {
    id: 'when',
    header: 'When',
    cell: (e) => <span className="whitespace-nowrap text-muted-foreground tabular-nums">{date(e.occurredAt)}</span>,
    sortValue: (e) => e.occurredAt,
    hideOnCard: true,
  },
  {
    id: 'action',
    header: 'Action',
    primary: true,
    cell: (e) => (
      <>
        <span className="font-mono text-xs">{e.action}</span>
        {e.outcome !== 'success' && (
          <span className="ml-2 rounded-full bg-[hsl(var(--warning)/0.15)] px-1.5 py-0.5 text-[0.6875rem]">
            {e.outcome}
          </span>
        )}
      </>
    ),
    sortValue: (e) => e.action,
  },
  {
    id: 'who',
    header: 'Who',
    cell: (e) => (
      <>
        {e.actorDisplay ?? '—'}
        {e.actorAttribution === 'propagated' && (
          <span
            className="ml-2 text-xs text-muted-foreground"
            title="Recorded from what a peer service said, on this person's behalf — not from a session it authenticated."
          >
            via service
          </span>
        )}
      </>
    ),
    sortValue: (e) => e.actorDisplay?.toLowerCase() ?? null,
  },
  {
    id: 'subject',
    header: 'Subject',
    cell: (e) => (
      <span className="text-muted-foreground">{e.resourceLabel ?? e.resourceType ?? '—'}</span>
    ),
    sortValue: (e) => e.resourceLabel ?? e.resourceType ?? null,
  },
  {
    id: 'trace',
    header: 'Trace',
    cell: (e) =>
      e.traceId ? (
        // Twelve characters is enough to quote and to match against a trace store, and the
        // full id is on the row for a copy.
        <span className="font-mono text-xs text-muted-foreground" title={e.traceId}>
          {e.traceId.slice(0, 12)}
        </span>
      ) : (
        <span className="text-muted-foreground">—</span>
      ),
    hideOnCard: true,
  },
];

export function AuditPage() {
  const q = useInfiniteQuery({
    queryKey: ['platform-audit'],
    initialPageParam: null as { occurredAt: string; seq: number } | null,
    queryFn: ({ pageParam }) => {
      const params = new URLSearchParams({ limit: '50' });
      if (pageParam) {
        params.set('beforeOccurredAt', pageParam.occurredAt);
        params.set('beforeSeq', String(pageParam.seq));
      }
      return platformApi.get<AuditPageResponse>(`/audit?${params.toString()}`);
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

      <DataTable
        rows={items}
        columns={columns}
        rowKey={(e) => e.id}
        isLoading={q.isLoading}
        caption="Platform audit log"
        empty={
          <EmptyState
            title="Nothing recorded yet"
            description="Platform-scope records appear here as soon as an operator acts."
          />
        }
      />

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
    </div>
  );
}
