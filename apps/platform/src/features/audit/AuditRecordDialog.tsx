import { useQuery } from '@tanstack/react-query';
import { Lock } from 'lucide-react';
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
import { platformApi } from '../../lib/api';
import { date } from '../../lib/format';

/** Everything a record holds. platform-api has already withheld whatever looks like a secret. */
interface AuditDetail {
  id: string;
  eventId: string;
  occurredAt: string;
  recordedAt: string;
  action: string;
  category: string;
  outcome: string;
  severity: number;
  isSandbox: boolean;
  actorKind: string;
  actorId: string | null;
  actorRef: string | null;
  actorDisplay: string | null;
  actorAttribution: string;
  subjectUserId: string | null;
  resourceType: string | null;
  resourceId: string | null;
  resourceLabel: string | null;
  service: string;
  serviceInstance: string;
  producerSeq: number;
  httpMethod: string | null;
  route: string | null;
  statusCode: number | null;
  ipAddress: string | null;
  ipTrusted: boolean;
  userAgent: string | null;
  correlationId: string | null;
  causationId: string | null;
  traceId: string | null;
  spanId: string | null;
  metadata: string | null;
  changes: string | null;
  redactionVersion: number;
  schemaVersion: number;
  chainKey: string;
  period: string;
  seq: number;
  hashVersion: number;
  prevHash: string | null;
  hash: string;
}

interface FieldChange {
  field: string;
  before: unknown;
  after: unknown;
  redacted: boolean;
}

const SEVERITY = ['info', 'notice', 'warning', 'critical'];

/**
 * One record, in full — opened from a row of the platform audit log. The list stays terse; this
 * is where the request, the chain position, the field diffs and the metadata are looked at.
 * The occurred_at travels with the id so platform-api reads one partition, not all of them.
 */
export function AuditRecordDialog({
  record,
  onClose,
}: {
  record: { id: string; occurredAt: string } | null;
  onClose: () => void;
}) {
  const query = useQuery({
    queryKey: ['platform-audit', 'record', record?.id],
    queryFn: () =>
      platformApi.get<AuditDetail>(
        `/audit/${record!.id}?${new URLSearchParams({ at: record!.occurredAt }).toString()}`,
      ),
    enabled: record !== null,
  });
  const r = query.data;

  return (
    <Dialog open={record !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent wide className="max-h-[85dvh]">
        <DialogHeader>
          <DialogTitle className="font-mono text-base">{r?.action ?? 'Audit record'}</DialogTitle>
          {r ? <DialogDescription>{date(r.occurredAt)}</DialogDescription> : null}
        </DialogHeader>
        <DialogBody>
          {query.isLoading ? (
            <CenteredSpinner />
          ) : query.isError || !r ? (
            <p className="py-8 text-center text-sm text-muted-foreground">This record could not be loaded.</p>
          ) : (
            <div className="space-y-6">
              <Section title="Event">
                <Fact label="Outcome">
                  <Badge
                    tone={r.outcome === 'success' ? 'success' : r.outcome === 'denied' ? 'warning' : 'destructive'}
                  >
                    {r.outcome}
                  </Badge>
                </Fact>
                <Fact label="Category">{r.category}</Fact>
                <Fact label="Severity">{SEVERITY[r.severity] ?? r.severity}</Fact>
                {r.isSandbox ? <Fact label="Sandbox">yes — a preview, not live data</Fact> : null}
              </Section>

              <Section title="Actor">
                <Fact label="Who">{r.actorDisplay ?? '—'}</Fact>
                <Fact label="Kind">
                  {r.actorKind} · {r.actorAttribution}
                </Fact>
                <Id label="Actor id" value={r.actorId} />
                <Id label="Actor ref" value={r.actorRef} />
                <Id label="About user" value={r.subjectUserId} />
              </Section>

              {r.resourceType || r.resourceId || r.resourceLabel ? (
                <Section title="Subject">
                  <Fact label="Type">{r.resourceType ?? '—'}</Fact>
                  <Fact label="Label">{r.resourceLabel ?? '—'}</Fact>
                  <Id label="Id" value={r.resourceId} />
                </Section>
              ) : null}

              <Section title="Request">
                {r.route ? (
                  <Fact label="Route">
                    <code className="text-xs break-all">
                      {r.httpMethod} {r.route}
                      {r.statusCode ? ` → ${r.statusCode}` : ''}
                    </code>
                  </Fact>
                ) : null}
                {r.ipAddress ? (
                  <Fact label="Address">
                    <code className="text-xs">{r.ipAddress}</code>
                    {/* Every service trusts forwarding headers today: the address is the caller's claim. */}
                    {r.ipTrusted ? null : <span className="ml-2 text-xs text-muted-foreground">(reported)</span>}
                  </Fact>
                ) : null}
                {r.userAgent ? (
                  <Fact label="User agent">
                    <span className="text-xs break-words">{r.userAgent}</span>
                  </Fact>
                ) : null}
                <Fact label="Service">
                  {r.service} <span className="text-xs text-muted-foreground">· {r.serviceInstance} #{r.producerSeq}</span>
                </Fact>
                <Id label="Correlation" value={r.correlationId} />
                <Id label="Caused by" value={r.causationId} />
                <Id label="Trace" value={r.traceId} />
                <Id label="Span" value={r.spanId} />
              </Section>

              <Changes changes={r.changes} redactionVersion={r.redactionVersion} />
              <Metadata metadata={r.metadata} />

              <Section title="Integrity">
                <Id label="Record id" value={r.id} />
                <Id label="Event id" value={r.eventId} />
                <Fact label="Recorded">{date(r.recordedAt)}</Fact>
                <Fact label="Chain">
                  <code className="text-xs break-all">{r.chainKey}</code>
                  <span className="ml-2 text-xs text-muted-foreground">
                    {r.period} · seq {r.seq}
                  </span>
                </Fact>
                <Id label="Previous hash" value={r.prevHash} />
                <Id label="Hash" value={r.hash} />
                <Fact label="Versions">
                  <span className="text-xs text-muted-foreground">
                    hash v{r.hashVersion} · redaction v{r.redactionVersion} · schema v{r.schemaVersion}
                  </span>
                </Fact>
              </Section>
            </div>
          )}
        </DialogBody>
      </DialogContent>
    </Dialog>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section>
      <h3 className="mb-2 text-sm font-medium">{title}</h3>
      <dl className="grid grid-cols-1 gap-x-4 gap-y-1.5 text-sm sm:grid-cols-[max-content_1fr]">{children}</dl>
    </section>
  );
}

function Fact({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="min-w-0">{children}</dd>
    </>
  );
}

/** An identifier: monospace, wraps anywhere, omitted when absent. */
function Id({ label, value }: { label: string; value: string | null }) {
  if (!value) return null;
  return (
    <Fact label={label}>
      <code className="text-xs break-all">{value}</code>
    </Fact>
  );
}

/** A withheld field is shown as changed with no values, never hidden: "the key changed" is a fact. */
function Changes({ changes, redactionVersion }: { changes: string | null; redactionVersion: number }) {
  let parsed: FieldChange[] = [];
  try {
    const raw: unknown = changes ? JSON.parse(changes) : null;
    if (Array.isArray(raw)) parsed = raw as FieldChange[];
  } catch {
    // A malformed payload is not a reason to hide the rest of the record.
  }
  if (parsed.length === 0) return null;

  return (
    <section>
      <h3 className="mb-2 text-sm font-medium">Changes</h3>
      <div className="overflow-x-auto rounded-md border">
        <table className="w-full text-sm">
          <thead className="border-b bg-muted/40 text-left text-xs text-muted-foreground">
            <tr>
              <th className="px-3 py-2 font-medium">Field</th>
              <th className="px-3 py-2 font-medium">Before</th>
              <th className="px-3 py-2 font-medium">After</th>
            </tr>
          </thead>
          <tbody>
            {parsed.map((c) => (
              <tr key={c.field} className="border-b align-top last:border-0">
                <td className="px-3 py-2 font-medium">{c.field}</td>
                {c.redacted ? (
                  <td className="px-3 py-2 text-muted-foreground" colSpan={2}>
                    <span className="inline-flex items-center gap-1.5">
                      <Lock className="h-3.5 w-3.5" />
                      Value withheld
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
      <p className="mt-1.5 text-xs text-muted-foreground">Redaction policy v{redactionVersion}.</p>
    </section>
  );
}

function Value({ value }: { value: unknown }) {
  if (value === null || value === undefined) {
    return <td className="px-3 py-2 italic text-muted-foreground">empty</td>;
  }
  return (
    <td className="max-w-xs px-3 py-2">
      <span className="break-words font-mono text-xs">
        {typeof value === 'object' ? JSON.stringify(value) : String(value)}
      </span>
    </td>
  );
}

/** Per-action by design, so formatted JSON rather than a table. */
function Metadata({ metadata }: { metadata: string | null }) {
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
      <h3 className="mb-2 text-sm font-medium">Metadata</h3>
      <pre className="overflow-x-auto whitespace-pre-wrap break-all rounded-md border bg-muted/40 p-3 text-xs">
        {pretty}
      </pre>
    </section>
  );
}
