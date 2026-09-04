import { useInfiniteQuery, useMutation } from '@tanstack/react-query';
import { Download, ShieldCheck, ShieldX } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CenteredSpinner,
  EmptyState,
  Input,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@dcms/admin-ui';
import { ApiError, api } from '../../lib/api';
import { Perm, can, useMyPermissions } from '../../lib/permissions';
import { AuditRecordDialog } from './AuditRecordDialog';
import type { AuditPage as AuditPageResult, AuditRecord, FieldChange, VerifySegment } from './types';

const CATEGORIES = ['tenantstate', 'auth', 'access', 'system', 'security'] as const;
const ANY = '__any';

/**
 * The audit log itself: filters, the table, paging, verify and export.
 *
 * <p>Split out of the page so the platform Tenants view can show one tenant's log in a dialog
 * without duplicating any of it. The two callers differ in exactly one thing — which tenant
 * the rows belong to — and that difference is the <code>tenant</code> prop.</p>
 *
 * <p><b>The tenant slug is part of every query key.</b> Without it react-query would serve the
 * currently-selected tenant's cached rows under another tenant's heading, which is a
 * particularly bad way for an audit log to be wrong.</p>
 */
export function AuditLogViewer({ tenant }: { tenant?: string }) {
  const { t } = useTranslation();
  const [action, setAction] = useState('');
  const [category, setCategory] = useState<string>(ANY);
  const [outcome, setOutcome] = useState<string>(ANY);
  const [openId, setOpenId] = useState<string | null>(null);

  const me = useMyPermissions(true);
  // Resolved against the *current* tenant. A platform admin reading another tenant's log is a
  // SuperAdmin, for whom this is true everywhere; a tenant user only ever sees their own.
  const canExport = can(me.data, Perm.AuditExport);

  const scope = tenant ? { tenant } : undefined;

  // The same filters the table is showing. An export that quietly ignored them would hand
  // someone more than they asked for and more than they think they got.
  const filterParams = () => {
    const params = new URLSearchParams();
    if (action) params.set('action', action);
    if (category !== ANY) params.set('category', category);
    if (outcome !== ANY) params.set('outcome', outcome);
    return params;
  };

  const query = useInfiniteQuery({
    queryKey: ['audit', tenant ?? null, action, category, outcome],
    initialPageParam: null as AuditPageResult['nextCursor'],
    queryFn: ({ pageParam }) => {
      const params = filterParams();
      if (pageParam) {
        params.set('beforeOccurredAt', pageParam.occurredAt);
        params.set('beforeSeq', String(pageParam.seq));
      }
      return api.get<AuditPageResult>(`/admin/audit?${params}`, scope);
    },
    getNextPageParam: (last) => (last.hasMore ? last.nextCursor : undefined),
  });

  const verify = useMutation({
    mutationFn: () =>
      api.get<{ valid: boolean; segments: VerifySegment[] }>('/admin/audit/verify', scope),
    onSuccess: (result) => {
      if (result.valid) {
        const checked = result.segments.reduce((sum, s) => sum + s.checked, 0);
        toast.success(t('audit.verifyOk', { count: checked }));
        return;
      }
      const broken = result.segments.find((s) => !s.valid);
      // Never auto-dismiss: a broken chain is the one message in this app that must
      // not scroll away before somebody reads it.
      toast.error(broken?.reason ?? t('audit.verifyFailed'), { duration: Infinity });
    },
    onError: () => toast.error(t('audit.verifyError')),
  });

  const exportLog = useMutation({
    mutationFn: async () => {
      const blob = await api.downloadBlob(`/admin/audit/export?${filterParams()}`, scope);
      // Anchor-and-revoke rather than navigating: the request needs the admin auth header,
      // which a plain link cannot carry.
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      // Name the file after the tenant it describes. Two exports in a downloads folder are
      // otherwise indistinguishable, and guessing wrong about whose log you are reading is
      // exactly the mistake this feature exists to prevent.
      const who = tenant ? `${tenant}-` : '';
      link.download = `audit-${who}${new Date().toISOString().slice(0, 10)}.ndjson`;
      link.click();
      URL.revokeObjectURL(url);
    },
    onError: () => toast.error(t('audit.exportError')),
  });

  const records = query.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <>
      <div className="mb-4 flex flex-wrap items-center gap-2">
        <Input
          className="w-64"
          placeholder={t('audit.filterAction')}
          value={action}
          onChange={(e) => setAction(e.target.value)}
        />
        <Select value={category} onValueChange={setCategory}>
          <SelectTrigger className="w-44">
            <SelectValue placeholder={t('audit.filterCategory')} />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ANY}>{t('audit.anyCategory')}</SelectItem>
            {CATEGORIES.map((c) => (
              <SelectItem key={c} value={c}>
                {t(`audit.category.${c}`, { defaultValue: c })}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={outcome} onValueChange={setOutcome}>
          <SelectTrigger className="w-40">
            <SelectValue placeholder={t('audit.filterOutcome')} />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ANY}>{t('audit.anyOutcome')}</SelectItem>
            <SelectItem value="success">{t('audit.outcome.success', { defaultValue: 'success' })}</SelectItem>
            <SelectItem value="denied">{t('audit.outcome.denied', { defaultValue: 'denied' })}</SelectItem>
            <SelectItem value="failure">{t('audit.outcome.failure', { defaultValue: 'failure' })}</SelectItem>
          </SelectContent>
        </Select>

        <div className="ml-auto flex items-center gap-2">
          {canExport ? (
            <Button variant="outline" onClick={() => exportLog.mutate()} disabled={exportLog.isPending}>
              <Download className="mr-2 h-4 w-4" />
              {t('audit.export')}
            </Button>
          ) : null}
          <Button variant="outline" onClick={() => verify.mutate()} disabled={verify.isPending}>
            {verify.data?.valid === false ? (
              <ShieldX className="mr-2 h-4 w-4" />
            ) : (
              <ShieldCheck className="mr-2 h-4 w-4" />
            )}
            {t('audit.verify')}
          </Button>
        </div>
      </div>

      {query.isLoading ? (
        <CenteredSpinner />
      ) : query.isError ? (
        // Distinguish "you may not read this" from "the server broke". Guessing at permissions
        // for what is actually a 500 sends the reader to check their roles while the real fault
        // sits in the logs — which is exactly what happened the first time this failed.
        <EmptyState
          title={t('audit.loadFailed')}
          description={
            query.error instanceof ApiError && query.error.status === 403
              ? t('audit.loadFailedForbidden')
              : t('audit.loadFailedHint')
          }
        />
      ) : records.length === 0 ? (
        <EmptyState title={t('audit.empty')} description={t('audit.emptyHint')} />
      ) : (
        <Card>
          <CardContent className="p-0">
            {/* Wide by nature; scroll the table rather than the page. */}
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead className="border-b text-left text-muted-foreground">
                  <tr>
                    <th className="px-4 py-2 font-medium">{t('audit.col.when')}</th>
                    <th className="px-4 py-2 font-medium">{t('audit.col.action')}</th>
                    <th className="px-4 py-2 font-medium">{t('audit.col.actor')}</th>
                    <th className="px-4 py-2 font-medium">{t('audit.col.resource')}</th>
                    <th className="px-4 py-2 font-medium">{t('audit.col.outcome')}</th>
                    <th className="px-4 py-2 font-medium">{t('audit.col.origin')}</th>
                  </tr>
                </thead>
                <tbody>
                  {records.map((r) => (
                    <AuditRow key={r.id} record={r} onOpen={() => setOpenId(r.id)} />
                  ))}
                </tbody>
              </table>
            </div>
          </CardContent>
        </Card>
      )}

      {query.hasNextPage ? (
        <div className="mt-4 flex justify-center">
          <Button
            variant="outline"
            onClick={() => void query.fetchNextPage()}
            disabled={query.isFetchingNextPage}
          >
            {t('audit.loadMore')}
          </Button>
        </div>
      ) : null}

      <AuditRecordDialog id={openId} tenant={tenant} onClose={() => setOpenId(null)} />
    </>
  );
}

function AuditRow({ record, onOpen }: { record: AuditRecord; onOpen: () => void }) {
  const { t } = useTranslation();

  return (
    <tr className="cursor-pointer border-b align-top last:border-0 hover:bg-muted/50" onClick={onOpen}>
      <td className="whitespace-nowrap px-4 py-2 tabular-nums text-muted-foreground">
        {new Date(record.occurredAt).toLocaleString()}
      </td>
      <td className="px-4 py-2">
        {/* Falls back to the raw key so an action added since the last translation pass
            still reads as something rather than as a blank cell. */}
        <span className="font-medium">{t(`audit.action.${record.action}`, { defaultValue: record.action })}</span>
        {record.isSandbox ? (
          <Badge tone="outline" className="ml-2">
            {t('audit.sandbox')}
          </Badge>
        ) : null}
      </td>
      <td className="px-4 py-2">
        <div>{record.actor.display ?? record.actor.ref ?? t('audit.actorUnknown')}</div>
        <div className="text-xs text-muted-foreground">
          {record.actor.kind}
          {/* An actor carried across a message bus is a peer's assertion, not something
              this request authenticated — worth saying so rather than implying certainty. */}
          {record.actor.attribution !== 'direct' ? ` · ${t(`audit.attribution.${record.actor.attribution}`, { defaultValue: record.actor.attribution })}` : ''}
        </div>
      </td>
      <td className="px-4 py-2">
        {record.resource ? (
          <>
            <div>{record.resource.label ?? record.resource.id}</div>
            <div className="text-xs text-muted-foreground">{record.resource.type}</div>
            <ChangedFields changes={record.changes} />
          </>
        ) : record.platformScope ? (
          <span className="text-xs text-muted-foreground">{t('audit.platformScope')}</span>
        ) : (
          <span className="text-muted-foreground">—</span>
        )}
      </td>
      <td className="px-4 py-2">
        <Badge tone={record.outcome === 'success' ? 'success' : record.outcome === 'denied' ? 'warning' : 'destructive'}>
          {t(`audit.outcome.${record.outcome}`, { defaultValue: record.outcome })}
        </Badge>
      </td>
      <td className="px-4 py-2 text-xs text-muted-foreground">
        <div>{record.service}</div>
        {record.ipAddress ? (
          // Every service that records an address currently trusts any forwarding proxy,
          // so the address is what the caller claimed unless it provably passed the edge.
          <div title={record.ipTrusted ? undefined : t('audit.ipReportedHint')}>
            {record.ipAddress}
            {record.ipTrusted ? '' : ` (${t('audit.ipReported')})`}
          </div>
        ) : null}
      </td>
    </tr>
  );
}

/**
 * The field names a record's diff mentions. Names only, on purpose: the values belong in the
 * detail view, and a row that dumped them would be unreadable at ten records, let alone two
 * hundred. A redacted field is still listed — "the API key changed" is the fact, and it is a
 * different fact from the key not having changed.
 */
const MAX_LISTED_FIELDS = 4;

function ChangedFields({ changes }: { changes: string | null }) {
  const { t } = useTranslation();

  const fields = useMemo(() => {
    if (!changes) return [];
    try {
      const parsed: unknown = JSON.parse(changes);
      if (!Array.isArray(parsed)) return [];
      return (parsed as FieldChange[]).filter((c) => typeof c?.field === 'string');
    } catch {
      // A record whose payload will not parse is still a record. Showing the rest of the row
      // beats hiding an audit entry because one column is malformed.
      return [];
    }
  }, [changes]);

  if (fields.length === 0) return null;

  const shown = fields.slice(0, MAX_LISTED_FIELDS);
  const rest = fields.length - shown.length;

  return (
    <div className="mt-1 text-xs text-muted-foreground">
      {shown.map((c) => c.field + (c.redacted ? ' 🔒' : '')).join(', ')}
      {rest > 0 ? ` ${t('audit.andMoreFields', { count: rest })}` : ''}
    </div>
  );
}
