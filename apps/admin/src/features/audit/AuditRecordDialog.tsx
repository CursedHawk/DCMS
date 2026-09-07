import { useQuery } from '@tanstack/react-query';
import { Lock } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  CenteredSpinner,
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@dcms/ui';
import { api } from '../../lib/api';
import type { AuditRecord, FieldChange } from './types';

/**
 * One record, in full.
 *
 * The list is deliberately terse — two hundred rows of field diffs is unreadable — so this is
 * where the before/after values and the metadata are actually looked at. It refetches the
 * record by id rather than reusing the list row: the list projection is trimmed, and a reader
 * who opened a record expects to see everything the platform is willing to show them.
 */
export function AuditRecordDialog({
  id,
  tenant,
  onClose,
}: {
  id: string | null;
  /** Read the record as this tenant rather than the selected one. See AuditLogViewer. */
  tenant?: string;
  onClose: () => void;
}) {
  const { t } = useTranslation();

  const query = useQuery({
    // The tenant belongs in the key for the same reason it does in the list: a record id is
    // only unique within the tenant that can see it, and the endpoint answers 404 rather than
    // 403 for anyone else's.
    queryKey: ['audit', 'record', tenant ?? null, id],
    queryFn: () => api.get<AuditRecord>(`/admin/audit/${id}`, tenant ? { tenant } : undefined),
    enabled: id !== null,
  });

  const record = query.data;

  return (
    <Dialog open={id !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent wide className="max-h-[85dvh]">
        <DialogHeader>
          <DialogTitle>
            {record
              ? t(`audit.action.${record.action}`, { defaultValue: record.action })
              : t('audit.record.loading')}
          </DialogTitle>
          {record ? (
            <DialogDescription>
              {new Date(record.occurredAt).toLocaleString()} · {record.action}
            </DialogDescription>
          ) : null}
        </DialogHeader>

        <DialogBody>
          {query.isLoading ? (
            <CenteredSpinner />
          ) : query.isError || !record ? (
            <p className="py-8 text-center text-sm text-muted-foreground">{t('audit.record.notFound')}</p>
          ) : (
            <div className="space-y-6">
              <Facts record={record} />
              <Changes changes={record.changes} redactionVersion={record.redactionVersion} />
              <Metadata metadata={record.metadata} />
            </div>
          )}
        </DialogBody>
      </DialogContent>
    </Dialog>
  );
}

function Facts({ record }: { record: AuditRecord }) {
  const { t } = useTranslation();

  return (
    <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-2 text-sm">
      <Fact label={t('audit.col.actor')}>
        <div>{record.actor.display ?? record.actor.ref ?? t('audit.actorUnknown')}</div>
        <div className="text-xs text-muted-foreground">
          {record.actor.kind}
          {record.actor.attribution !== 'direct'
            ? ` · ${t(`audit.attribution.${record.actor.attribution}`, { defaultValue: record.actor.attribution })}`
            : ''}
        </div>
      </Fact>

      {record.resource ? (
        <Fact label={t('audit.col.resource')}>
          <div>{record.resource.label ?? record.resource.id ?? '—'}</div>
          <div className="text-xs text-muted-foreground">
            {record.resource.type}
            {record.resource.id ? ` · ${record.resource.id}` : ''}
          </div>
        </Fact>
      ) : null}

      <Fact label={t('audit.col.outcome')}>
        <Badge
          tone={
            record.outcome === 'success' ? 'success' : record.outcome === 'denied' ? 'warning' : 'destructive'
          }
        >
          {t(`audit.outcome.${record.outcome}`, { defaultValue: record.outcome })}
        </Badge>
      </Fact>

      <Fact label={t('audit.col.origin')}>
        <div>{record.service}</div>
        {record.http?.route ? (
          <div className="text-xs text-muted-foreground">
            {record.http.method} {record.http.route}
            {record.http.status ? ` → ${record.http.status}` : ''}
          </div>
        ) : null}
        {record.ipAddress ? (
          <div className="text-xs text-muted-foreground">
            {record.ipAddress}
            {record.ipTrusted ? '' : ` (${t('audit.ipReported')})`}
          </div>
        ) : null}
      </Fact>

      {record.correlationId ? (
        // The one field worth copying into a support ticket: it ties this record to the
        // browser action that caused it and to everything that action set off downstream.
        <Fact label={t('audit.record.correlation')}>
          <code className="text-xs">{record.correlationId}</code>
        </Fact>
      ) : null}

      {record.platformScope ? (
        <Fact label={t('audit.record.scope')}>
          <span className="text-xs text-muted-foreground">{t('audit.record.platformScopeHint')}</span>
        </Fact>
      ) : null}
    </dl>
  );
}

function Fact({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="min-w-0 break-words">{children}</dd>
    </>
  );
}

/**
 * The before/after table.
 *
 * A redacted field is shown as changed with both values withheld, never hidden: "the API key
 * changed" and "the API key did not change" are different facts, and collapsing them would
 * make the diff quietly lie.
 */
function Changes({ changes, redactionVersion }: { changes: string | null; redactionVersion?: number }) {
  const { t } = useTranslation();

  let parsed: FieldChange[] = [];
  if (changes) {
    try {
      const raw: unknown = JSON.parse(changes);
      if (Array.isArray(raw)) parsed = raw as FieldChange[];
    } catch {
      // A malformed payload is not a reason to hide the rest of the record.
    }
  }

  if (parsed.length === 0) return null;

  return (
    <section>
      <h3 className="mb-2 text-sm font-medium">{t('audit.record.changes')}</h3>
      <div className="overflow-x-auto rounded-md border">
        <table className="w-full text-sm">
          <thead className="border-b bg-muted/40 text-left text-xs text-muted-foreground">
            <tr>
              <th className="px-3 py-2 font-medium">{t('audit.record.field')}</th>
              <th className="px-3 py-2 font-medium">{t('audit.record.before')}</th>
              <th className="px-3 py-2 font-medium">{t('audit.record.after')}</th>
            </tr>
          </thead>
          <tbody>
            {parsed.map((c) => (
              <tr key={c.field} className="border-b last:border-0 align-top">
                <td className="px-3 py-2 font-medium">{c.field}</td>
                {c.redacted ? (
                  <td className="px-3 py-2 text-muted-foreground" colSpan={2}>
                    <span className="inline-flex items-center gap-1.5">
                      <Lock className="h-3.5 w-3.5" />
                      {t('audit.record.redacted')}
                    </span>
                  </td>
                ) : (
                  <>
                    <Value value={c.before} />
                    <Value value={c.after} />
                  </>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {redactionVersion ? (
        <p className="mt-1.5 text-xs text-muted-foreground">
          {t('audit.record.redactionVersion', { version: redactionVersion })}
        </p>
      ) : null}
    </section>
  );
}

function Value({ value }: { value: unknown }) {
  const { t } = useTranslation();

  if (value === null || value === undefined) {
    return <td className="px-3 py-2 text-muted-foreground italic">{t('audit.record.empty')}</td>;
  }
  return (
    <td className="max-w-xs px-3 py-2">
      <span className="break-words font-mono text-xs">{String(value)}</span>
    </td>
  );
}

/**
 * Whatever the action chose to record: bulk counts, commit shas, the permission keys a role
 * gained. Rendered as formatted JSON rather than a table because the shape is per-action by
 * design — that is what makes the log extensible without a migration.
 */
function Metadata({ metadata }: { metadata: string | null }) {
  const { t } = useTranslation();

  if (!metadata) return null;

  let pretty = metadata;
  try {
    const parsed: unknown = JSON.parse(metadata);
    if (parsed && typeof parsed === 'object' && Object.keys(parsed).length === 0) return null;
    pretty = JSON.stringify(parsed, null, 2);
  } catch {
    // Show it raw rather than nothing.
  }

  return (
    <section>
      <h3 className="mb-2 text-sm font-medium">{t('audit.record.metadata')}</h3>
      <pre className="overflow-x-auto rounded-md border bg-muted/40 p-3 text-xs">{pretty}</pre>
    </section>
  );
}
