import { useState } from 'react';
import { AlertTriangle, Globe, Plus, RefreshCw, ShieldCheck } from 'lucide-react';
import {
  Badge, Button, CenteredSpinner, Dialog, DialogBody, DialogContent, DialogDescription,
  DialogFooter, DialogHeader, DialogTitle, EmptyState, Input, Label, Switch, TagsInput,
  toastApiError,
} from '@dcms/admin-ui';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { date } from '../../lib/format';
import { can, useMe, Perm } from '../../lib/permissions';
import {
  useCertificateAttempts, useCreateCertificate, useDeleteCertificate, useManagedCertificates,
  useReissueCertificate, useUpdateCertificate, type ManagedCertificate,
} from './api';

/** The row being edited, or a blank one for "add". */
type Editing = { id: string | null; name: string; identifiers: string[]; enabled: boolean };

const BLANK: Editing = { id: null, name: '', identifiers: [], enabled: true };

/** Let's Encrypt's duplicate-certificate limit: 5 per identical set of names per 7 days. */
const LE_WEEKLY_LIMIT = 5;

/**
 * What the edge will actually spend, kept under the CA's limit with headroom. Mirrors
 * CertificateOptions.ManagedIssuancesPerWeek; if that is retuned, change this to match — it is
 * shown to an operator deciding whether pressing "Renew now" will do anything.
 */
const WEEKLY_CEILING = 3;

/**
 * The domains DCMS keeps TLS certificates for on its own behalf.
 *
 * <p>This is the platform's own estate, not a tenant's. A tenant's custom domain is issued
 * per-hostname over HTTP-01, or they upload their own certificate, and both are managed from the
 * tenant admin console — nothing here renews or replaces one. What this page controls is which
 * names the platform itself holds certificates for, which since ADR 0011 is a table rather than
 * a compiled-in list.</p>
 */
export function CertificatesPage() {
  const { t } = useTranslation();
  const me = useMe(true);
  const certificates = useManagedCertificates();
  const create = useCreateCertificate();
  const update = useUpdateCertificate();
  const remove = useDeleteCertificate();
  const reissue = useReissueCertificate();

  const mayManage = can(me.data, Perm.CertificatesManage);
  const onError = (e: unknown) => toastApiError(e, t);

  const [editing, setEditing] = useState<Editing | null>(null);
  const [deleting, setDeleting] = useState<ManagedCertificate | null>(null);
  const [showingAttempts, setShowingAttempts] = useState<string | null>(null);
  const attempts = useCertificateAttempts(showingAttempts);

  if (certificates.isLoading) return <CenteredSpinner />;

  const rows = certificates.data ?? [];

  const save = () => {
    if (!editing) return;
    const body = { name: editing.name, identifiers: editing.identifiers, enabled: editing.enabled };
    const done = () => {
      toast.success(editing.id ? 'Certificate updated.' : 'Certificate added.');
      setEditing(null);
    };
    if (editing.id) {
      update.mutate({ id: editing.id, ...body }, { onSuccess: done, onError });
    } else {
      create.mutate(body, { onSuccess: done, onError });
    }
  };

  return (
    <div className="mx-auto w-full max-w-4xl px-6 py-8">
      <header className="mb-6 flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Certificates</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            The domains this platform holds TLS certificates for, and keeps renewed. Tenants’
            own domains are not managed here.
          </p>
        </div>
        {mayManage && (
          <Button onClick={() => setEditing(BLANK)}>
            <Plus className="h-4 w-4" aria-hidden /> Add
          </Button>
        )}
      </header>

      {/* Stated once, plainly, because it is the thing that surprises people: one certificate
          can cover an unlimited number of subdomains, and that is why the platform is not
          issuing one per tenant site. */}
      <div className="mb-8 flex items-start gap-3 rounded-md border border-border bg-card p-4">
        <ShieldCheck className="mt-0.5 h-4 w-4 shrink-0 text-[hsl(var(--success))]" aria-hidden />
        <p className="text-sm text-muted-foreground">
          A wildcard such as <code>*.dcms.highgeek.eu</code> covers every subdomain under it, so
          one certificate serves every provisioned site. Wildcards are validated over DNS, which
          needs a Cloudflare token in the edge’s Vault path — without it they cannot be issued at
          all. Let’s Encrypt allows {LE_WEEKLY_LIMIT} certificates per week for the same set of
          names, so DCMS orders at most {WEEKLY_CEILING} and shows you how many are left.
        </p>
      </div>

      {rows.length === 0 ? (
        <EmptyState
          icon={Globe}
          title="No managed certificates"
          description="Add one to have the platform issue and renew a certificate for its own domains."
        />
      ) : (
        <ul className="space-y-4">
          {rows.map((row) => (
            <li key={row.id} className="rounded-md border border-border bg-card p-4">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <h2 className="font-medium">{row.name}</h2>
                    {!row.enabled && <Badge tone="secondary">Disabled</Badge>}
                    {row.requiresDns && <Badge tone="outline">DNS-01</Badge>}
                    {row.reissueRequested && <Badge tone="warning">Reissue pending</Badge>}
                  </div>
                  <div className="mt-2 flex flex-wrap gap-1">
                    {row.identifiers.map((i) => (
                      <Badge key={i} tone="outline" className="font-mono">{i}</Badge>
                    ))}
                  </div>
                </div>
                {mayManage && (
                  <div className="flex shrink-0 gap-2">
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={!row.enabled || reissue.isPending}
                      onClick={() =>
                        reissue.mutate(row.id, {
                          onSuccess: () => toast.success('Reissue requested.'),
                          onError,
                        })
                      }
                    >
                      <RefreshCw className="h-3.5 w-3.5" aria-hidden /> Renew now
                    </Button>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() =>
                        setEditing({
                          id: row.id,
                          name: row.name,
                          identifiers: row.identifiers,
                          enabled: row.enabled,
                        })
                      }
                    >
                      Edit
                    </Button>
                    <Button variant="outline" size="sm" onClick={() => setDeleting(row)}>
                      Remove
                    </Button>
                  </div>
                )}
              </div>

              <dl className="mt-4 grid grid-cols-2 gap-x-6 gap-y-2 text-sm sm:grid-cols-4">
                <Fact label="Issuer" value={row.issuer ?? '—'} />
                <Fact
                  label="Expires"
                  value={row.notAfter ? date(row.notAfter) : 'Not issued yet'}
                  tone={row.expired ? 'destructive' : undefined}
                />
                <Fact
                  label="Days left"
                  value={row.daysRemaining === null ? '—' : String(row.daysRemaining)}
                  tone={row.daysRemaining !== null && row.daysRemaining < 15 ? 'warning' : undefined}
                />
                <Fact
                  label="Issued this week"
                  // The number that decides whether "Renew now" will do anything. Shown next to
                  // the limit rather than alone, because 2 means nothing without the 3.
                  value={`${row.issuedThisWeek} of ${WEEKLY_CEILING}`}
                  tone={row.issuedThisWeek >= WEEKLY_CEILING ? 'warning' : undefined}
                />
              </dl>

              {row.covers.length > 0 && row.covers.join() !== row.identifiers.join() && (
                <p className="mt-3 text-xs text-muted-foreground">
                  The certificate currently served covers{' '}
                  <span className="font-mono">{row.covers.join(', ')}</span> — it will be replaced
                  with one matching the list above on the next renewal.
                </p>
              )}

              {row.lastError && (
                <div className="mt-3 flex items-start gap-2 rounded-md border border-destructive/30 bg-destructive/5 p-3">
                  <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" aria-hidden />
                  <div className="min-w-0 text-sm">
                    <p className="font-medium text-destructive">Last attempt failed</p>
                    {/* The CA's own sentence, kept verbatim. It is usually the only thing that
                        says which DNS record is wrong. */}
                    <p className="mt-0.5 break-words text-muted-foreground">{row.lastError}</p>
                  </div>
                </div>
              )}

              <button
                type="button"
                className="mt-3 text-xs text-muted-foreground underline underline-offset-2 hover:text-foreground"
                onClick={() => setShowingAttempts(showingAttempts === row.id ? null : row.id)}
              >
                {showingAttempts === row.id ? 'Hide history' : 'Show attempt history'}
              </button>

              {showingAttempts === row.id && (
                <div className="mt-2 space-y-1 text-xs">
                  {attempts.isLoading && <p className="text-muted-foreground">Loading…</p>}
                  {attempts.data?.length === 0 && (
                    <p className="text-muted-foreground">
                      Never attempted. The edge orders on its hourly sweep, or immediately when
                      you press “Renew now”.
                    </p>
                  )}
                  {attempts.data?.map((a, i) => (
                    <div key={i} className="flex flex-wrap items-baseline gap-2">
                      <span className="text-muted-foreground">{date(a.attemptedAt)}</span>
                      <Badge tone={a.succeeded ? 'success' : 'destructive'}>
                        {a.succeeded ? 'issued' : 'failed'}
                      </Badge>
                      {a.error && <span className="break-words text-muted-foreground">{a.error}</span>}
                    </div>
                  ))}
                </div>
              )}
            </li>
          ))}
        </ul>
      )}

      <Dialog open={editing !== null} onOpenChange={(open) => !open && setEditing(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{editing?.id ? 'Edit certificate' : 'Add certificate'}</DialogTitle>
          </DialogHeader>
          <DialogBody className="space-y-4">
            <div className="space-y-1.5">
              <Label htmlFor="cert-name">Name</Label>
              <Input
                id="cert-name"
                value={editing?.name ?? ''}
                placeholder="Platform wildcard"
                onChange={(e) => setEditing((s) => s && { ...s, name: e.target.value })}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="cert-identifiers">Domains</Label>
              <TagsInput
                id="cert-identifiers"
                value={editing?.identifiers ?? []}
                onChange={(identifiers) => setEditing((s) => s && { ...s, identifiers })}
                placeholder="example.com or *.example.com"
              />
              <p className="text-xs text-muted-foreground">
                A wildcard covers exactly one level, so <code>*.example.com</code> covers neither
                <code> example.com</code> itself nor <code>a.b.example.com</code>. List each one
                you need. The zone must be in the platform’s Cloudflare account.
              </p>
            </div>
            <div className="flex items-center gap-3">
              <Switch
                id="cert-enabled"
                checked={editing?.enabled ?? true}
                onCheckedChange={(enabled) => setEditing((s) => s && { ...s, enabled })}
              />
              <Label htmlFor="cert-enabled" className="font-normal">
                Keep this certificate renewed
              </Label>
            </div>
          </DialogBody>
          <DialogFooter>
            <Button variant="outline" onClick={() => setEditing(null)}>Cancel</Button>
            <Button onClick={save} disabled={create.isPending || update.isPending}>Save</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* A plain confirmation, not the type-the-name dialog used for tenants and sites. That
          one exists for deletes that destroy work with no backup; this one stops a renewal and
          leaves the certificate serving, so demanding the name back would misrepresent it. */}
      <Dialog open={deleting !== null} onOpenChange={(open) => !open && setDeleting(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Stop renewing {deleting?.name}?</DialogTitle>
            <DialogDescription>
              The certificate it already issued keeps being served until it expires
              {deleting?.notAfter ? ` on ${date(deleting.notAfter)}` : ''}, so nothing goes
              offline now — but nothing will replace it either. To pause renewal reversibly,
              disable it instead.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setDeleting(null)}>Cancel</Button>
            <Button
              variant="destructive"
              disabled={remove.isPending}
              onClick={() =>
                deleting &&
                remove.mutate(deleting.id, {
                  onSuccess: () => {
                    toast.success('No longer renewed.');
                    setDeleting(null);
                  },
                  onError,
                })
              }
            >
              Stop renewing
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

function Fact({ label, value, tone }: { label: string; value: string; tone?: 'warning' | 'destructive' }) {
  const color =
    tone === 'destructive' ? 'text-destructive'
      : tone === 'warning' ? 'text-[hsl(var(--warning))]'
        : '';
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className={`mt-0.5 ${color}`}>{value}</dd>
    </div>
  );
}
