import { useState } from 'react';
import { ExternalLink } from 'lucide-react';
import {
  Button, Dialog, DialogBody, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
  Input, Label, toastApiError,
} from '@dcms/ui';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { bytes, ofLimit, railState } from '../../lib/format';
import { runtimeConfig } from '../../runtime-config';
import { useSetStorageQuota, type TenantRow } from './api';

const GB = 1024 ** 3;

export function StatusPill({ status }: { status: string }) {
  const suspended = status === 'Suspended';
  return (
    <span
      className={
        suspended
          ? 'inline-flex items-center gap-1.5 rounded-full bg-[hsl(var(--warning)/0.15)] px-2 py-0.5 text-xs text-foreground'
          : 'inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs text-muted-foreground'
      }
    >
      <span
        aria-hidden
        className={`inline-block h-1.5 w-1.5 rounded-full ${suspended ? 'bg-[hsl(var(--warning))]' : 'bg-[hsl(var(--success))]'}`}
      />
      {suspended ? 'Suspended' : 'Active'}
    </span>
  );
}

/** Used of cap, with the console's capacity rail under it. */
export function StorageCell({ row }: { row: TenantRow }) {
  const fraction = Math.min(row.storageBytes / row.quotaBytes, 1);
  return (
    <span className="inline-flex w-28 flex-col items-end gap-1">
      <span className="font-mono">{ofLimit(row.storageBytes, row.quotaBytes)}</span>
      <span className="rail block h-1.5 w-full" aria-hidden>
        <span className="rail-fill block" data-state={railState(fraction)} style={{ width: `${fraction * 100}%` }} />
      </span>
    </span>
  );
}

export function StorageLimitDialog({ row, onClose }: { row: TenantRow; onClose: () => void }) {
  const { t } = useTranslation();
  const setQuota = useSetStorageQuota();
  const [gb, setGb] = useState(String(+(row.quotaBytes / GB).toFixed(2)));
  const value = Number(gb);
  const valid = Number.isFinite(value) && value > 0 && value <= 100 * 1024;
  const below = valid && value * GB < row.storageBytes;

  const save = () =>
    setQuota.mutate(
      { tenantId: row.tenantId, quotaBytes: Math.round(value * GB) },
      {
        onSuccess: () => {
          toast.success(`${row.slug} can now store ${bytes(Math.round(value * GB))}`);
          onClose();
        },
        onError: (e) => toastApiError(e, t),
      },
    );

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Storage limit for {row.slug}</DialogTitle>
          <DialogDescription>
            Every file in the media library counts, originals and optimized versions together. Once
            the limit is reached, new uploads are refused. It uses {bytes(row.storageBytes)} now.
          </DialogDescription>
        </DialogHeader>
        <DialogBody className="space-y-1.5">
          <Label htmlFor="storage-limit">Limit in GB</Label>
          <Input
            id="storage-limit"
            type="number"
            inputMode="decimal"
            min={0.01}
            step="any"
            className="font-mono"
            value={gb}
            onChange={(e) => setGb(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && valid && !setQuota.isPending && save()}
          />
          <p className="text-xs text-muted-foreground">
            {below
              ? 'That is less than it already stores. Nothing is deleted, but no upload will succeed until files are removed.'
              : 'The default for a new tenant is 5 GB.'}
          </p>
        </DialogBody>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>Cancel</Button>
          <Button onClick={save} disabled={!valid || setQuota.isPending}>Save limit</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/*
 * Media and content are managed in the tenant admin, not here. Rather than rebuild those screens
 * against a cross-tenant API, the console hands the operator a link that opens the admin SPA
 * already pointed at this tenant.
 */
export function OpenInAdmin({ slug }: { slug: string }) {
  return (
    <a
      href={`${runtimeConfig.adminBase}/media?tenant=${encodeURIComponent(slug)}`}
      target="_blank"
      rel="noreferrer"
      className="inline-flex h-8 items-center gap-1.5 whitespace-nowrap rounded-md px-2 text-xs text-muted-foreground hover:bg-accent hover:text-accent-foreground"
    >
      Open in admin
      <ExternalLink className="h-3 w-3" aria-hidden />
    </a>
  );
}
