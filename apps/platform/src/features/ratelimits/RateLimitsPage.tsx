import { useState } from 'react';
import { Plus, ShieldOff, Trash2 } from 'lucide-react';
import {
  Button, CenteredSpinner, Dialog, DialogBody, DialogContent, DialogDescription, DialogFooter,
  DialogHeader, DialogTitle, EmptyState, Input, Label, toastApiError,
} from '@dcms/ui';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { date } from '../../lib/format';
import { can, useMe, Perm } from '../../lib/permissions';
import {
  useAddRateLimitExemption, useRateLimitExemptions, useRemoveRateLimitExemption,
  type RateLimitExemption,
} from './api';

const BLANK = { address: '', note: '' };

/**
 * Client addresses and ranges the platform does not rate-limit — a load generator, an uptime
 * probe, a partner's egress range. Enforced at the edge on the connection's real address, and
 * passed on to the services behind it, so their own limiters stand aside too. Changes apply
 * within seconds; nothing restarts.
 */
export function RateLimitsPage() {
  const { t } = useTranslation();
  const me = useMe(true);
  const exemptions = useRateLimitExemptions();
  const add = useAddRateLimitExemption();
  const remove = useRemoveRateLimitExemption();

  const mayManage = can(me.data, Perm.RateLimitsManage);
  const onError = (e: unknown) => toastApiError(e, t);

  const [adding, setAdding] = useState<typeof BLANK | null>(null);
  const [removing, setRemoving] = useState<RateLimitExemption | null>(null);

  if (exemptions.isLoading) return <CenteredSpinner />;
  const rows = exemptions.data ?? [];

  const save = () =>
    adding &&
    add.mutate(adding, {
      onSuccess: (row) => {
        toast.success(`${row.cidr} is exempt from rate limiting.`);
        setAdding(null);
      },
      onError,
    });

  return (
    <div className="mx-auto w-full max-w-4xl px-6 py-8">
      <header className="mb-6 flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Rate limits</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Addresses the platform does not rate-limit. Every other client is held to the edge’s
            per-address limit and each service’s own.
          </p>
        </div>
        {mayManage && (
          <Button onClick={() => setAdding(BLANK)}>
            <Plus className="h-4 w-4" aria-hidden /> Add exemption
          </Button>
        )}
      </header>

      <div className="mb-8 rounded-md border border-border bg-card p-4 text-sm text-muted-foreground">
        Matched against the connection’s real address at the edge — never a header a client
        could set. Sign-in stays limited even for exempt addresses: an exemption grants capacity,
        not unlimited password attempts. Changes take effect within seconds.
      </div>

      {rows.length === 0 ? (
        <EmptyState
          icon={ShieldOff}
          title="No exemptions"
          description="Every client is rate-limited. Add one for a load generator or a monitoring probe."
        />
      ) : (
        <ul className="divide-y divide-border rounded-md border border-border bg-card">
          {rows.map((row) => (
            <li key={row.id} className="flex items-start justify-between gap-4 p-4">
              <div className="min-w-0">
                <div className="font-mono text-sm">{row.cidr}</div>
                <p className="mt-1 text-sm">{row.note}</p>
                <p className="mt-1 text-xs text-muted-foreground">
                  Added {date(row.createdAt)}{row.createdBy ? ` by ${row.createdBy}` : ''}
                </p>
              </div>
              {mayManage && (
                <Button
                  variant="outline"
                  size="sm"
                  aria-label={`Remove exemption for ${row.cidr}`}
                  onClick={() => setRemoving(row)}
                >
                  <Trash2 className="h-4 w-4" aria-hidden /> Remove
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}

      <Dialog open={adding !== null} onOpenChange={(open) => !open && setAdding(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Exempt an address from rate limiting</DialogTitle>
            <DialogDescription>
              A single address or a CIDR range. The widest range accepted is /8 for IPv4 and /32
              for IPv6.
            </DialogDescription>
          </DialogHeader>
          <DialogBody className="space-y-4">
            <div className="space-y-1.5">
              <Label htmlFor="exemption-address">Address or range</Label>
              <Input
                id="exemption-address"
                className="font-mono"
                value={adding?.address ?? ''}
                placeholder="203.0.113.7 or 203.0.113.0/24"
                onChange={(e) => setAdding((s) => s && { ...s, address: e.target.value })}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="exemption-note">Why</Label>
              <Input
                id="exemption-note"
                value={adding?.note ?? ''}
                maxLength={500}
                placeholder="Load generator on the ops box"
                onChange={(e) => setAdding((s) => s && { ...s, note: e.target.value })}
              />
              <p className="text-xs text-muted-foreground">
                Required. An exemption nobody can explain is one nobody dares remove.
              </p>
            </div>
          </DialogBody>
          <DialogFooter>
            <Button variant="outline" onClick={() => setAdding(null)}>Cancel</Button>
            <Button
              onClick={save}
              disabled={add.isPending || !adding?.address.trim() || !adding?.note.trim()}
            >
              Exempt
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={removing !== null} onOpenChange={(open) => !open && setRemoving(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Rate-limit {removing?.cidr} again?</DialogTitle>
            <DialogDescription>
              Requests from it are limited again within seconds. A load test running from it now
              will start receiving 429s.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setRemoving(null)}>Cancel</Button>
            <Button
              variant="destructive"
              disabled={remove.isPending}
              onClick={() =>
                removing &&
                remove.mutate(removing.id, {
                  onSuccess: () => {
                    toast.success(`${removing.cidr} is rate-limited again.`);
                    setRemoving(null);
                  },
                  onError,
                })
              }
            >
              Remove exemption
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
