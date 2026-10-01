import { useMemo, useState } from 'react';
import { ExternalLink, Pencil } from 'lucide-react';
import {
  Button, ConfirmDeleteDialog, DataTable, Dialog, DialogBody, DialogContent, DialogDescription,
  DialogFooter, DialogHeader, DialogTitle, EmptyState, FilterBar, Input, Label, toastApiError,
  type Column,
} from '@dcms/ui';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { bytes, count, date, ofLimit, railState } from '../../lib/format';
import { can, useMe, Perm } from '../../lib/permissions';
import { runtimeConfig } from '../../runtime-config';
import { useSetStorageQuota, useSetTenantStatus, useTenants, type TenantRow } from './api';

const GB = 1024 ** 3;

export function TenantsPage() {
  const { t } = useTranslation();
  const [search, setSearch] = useState('');
  const [confirming, setConfirming] = useState<TenantRow | null>(null);
  const [limiting, setLimiting] = useState<TenantRow | null>(null);
  const me = useMe(true);
  const tenants = useTenants(search);
  const setStatus = useSetTenantStatus();

  const mayChange = can(me.data, Perm.TenantsLifecycle);
  const maySetLimits = can(me.data, Perm.TenantsWrite);

  const columns: Column<TenantRow>[] = useMemo(
    () => [
      {
        id: 'tenant',
        header: 'Tenant',
        primary: true,
        sortValue: (r) => r.slug,
        cell: (r) => (
          <div className="flex flex-col">
            <span className="font-mono">{r.slug}</span>
            {r.name && <span className="text-xs text-muted-foreground">{r.name}</span>}
          </div>
        ),
      },
      { id: 'status', header: 'Status', sortValue: (r) => r.status, cell: (r) => <StatusPill status={r.status} /> },
      {
        id: 'sites',
        header: 'Sites',
        align: 'right',
        sortValue: (r) => r.sites,
        cell: (r) => <span className="font-mono">{count(r.sites)}</span>,
      },
      {
        id: 'content',
        header: 'Content',
        srHeader: 'Published of total content items',
        align: 'right',
        sortValue: (r) => r.contentItems,
        cell: (r) => (
          <span className="font-mono">
            {count(r.publishedItems)} / {count(r.contentItems)}
          </span>
        ),
      },
      {
        id: 'storage',
        header: 'Storage',
        align: 'right',
        // By how full, not by bytes: "who is about to hit the limit" is the question here.
        sortValue: (r) => r.storageBytes / r.quotaBytes,
        cell: (r) => (
          <StorageCell row={r} onEdit={maySetLimits ? () => setLimiting(r) : undefined} />
        ),
      },
      {
        id: 'members',
        header: 'Members',
        align: 'right',
        sortValue: (r) => r.members,
        cell: (r) => <span className="font-mono">{count(r.members)}</span>,
      },
      {
        id: 'created',
        header: 'Created',
        sortValue: (r) => r.createdAt,
        cell: (r) => <span className="text-muted-foreground">{date(r.createdAt)}</span>,
      },
      {
        id: 'actions',
        header: '',
        srHeader: 'Actions',
        align: 'right',
        cell: (r) => (
          <div className="flex items-center justify-end gap-1">
            {/*
              Media and content are managed in the tenant admin, not here. Rather than rebuild
              those screens against a cross-tenant API, the console hands the operator a link
              that opens the admin SPA already pointed at this tenant.
            */}
            <a
              href={`${runtimeConfig.adminBase}/media?tenant=${encodeURIComponent(r.slug)}`}
              target="_blank"
              rel="noreferrer"
              className="inline-flex h-8 items-center gap-1.5 rounded-md px-2 text-xs text-muted-foreground hover:bg-accent hover:text-accent-foreground"
            >
              Open in admin
              <ExternalLink className="h-3 w-3" aria-hidden />
            </a>

            {mayChange &&
              (r.status === 'Suspended' ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={setStatus.isPending}
                  onClick={() =>
                    setStatus.mutate(
                      { tenantId: r.tenantId, suspend: false },
                      {
                        onSuccess: () => toast.success(`Resumed ${r.slug}`),
                        onError: (e) => toastApiError(e, t),
                      },
                    )
                  }
                >
                  Resume
                </Button>
              ) : (
                <Button variant="outline" size="sm" onClick={() => setConfirming(r)}>
                  Suspend
                </Button>
              ))}
          </div>
        ),
      },
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [mayChange, maySetLimits, setStatus.isPending],
  );

  return (
    <div className="mx-auto w-full max-w-5xl px-4 py-6 sm:px-6 sm:py-8">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold tracking-tight">Tenants</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Every workspace on this platform. Suspending one stops its admin access and takes its
          published sites offline.
        </p>
      </header>

      <FilterBar
        search={search}
        onSearchChange={setSearch}
        searchPlaceholder="Search by slug or name"
      />

      <DataTable
        rows={tenants.data}
        columns={columns}
        rowKey={(r) => r.tenantId}
        isLoading={tenants.isLoading}
        caption="Workspaces on this platform"
        defaultSort={{ columnId: 'tenant' }}
        empty={
          <EmptyState
            title={search ? 'No tenant matches that' : 'No tenants yet'}
            description={
              search
                ? 'Try part of the slug instead.'
                : 'Tenants are created from the tenant admin, or by an operator with tenant:write.'
            }
          />
        }
      />

      {limiting && <StorageLimitDialog row={limiting} onClose={() => setLimiting(null)} />}

      {/*
        Type-to-confirm, reusing the admin SPA's dialog. Suspension is reversible, so this is
        not the ceremony a deletion gets — but it takes a paying tenant's public site offline,
        which is not something to do by mis-clicking a row.
      */}
      {confirming && (
        <ConfirmDeleteDialog
          open
          onOpenChange={(open) => !open && setConfirming(null)}
          title={`Suspend ${confirming.slug}?`}
          description="Type the tenant's slug to confirm. You can resume it from this page afterwards."
          confirmationValue={confirming.slug}
          confirmLabel="Suspend tenant"
          pending={setStatus.isPending}
          consequences={[
            'Everyone in this workspace loses admin access immediately.',
            `Its published sites stop serving${confirming.sites > 0 ? ` (${count(confirming.sites)} site${confirming.sites === 1 ? '' : 's'})` : ''}.`,
            'Nothing is deleted. You can resume it from this page.',
          ]}
          onConfirm={() =>
            setStatus.mutate(
              { tenantId: confirming.tenantId, suspend: true },
              {
                onSuccess: () => {
                  toast.success(`Suspended ${confirming.slug}`);
                  setConfirming(null);
                },
                onError: (e) => toastApiError(e, t),
              },
            )
          }
        />
      )}
    </div>
  );
}

function StatusPill({ status }: { status: string }) {
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

/** Used of cap, with the console's capacity rail under it. A button when the cap is editable. */
function StorageCell({ row, onEdit }: { row: TenantRow; onEdit?: () => void }) {
  const fraction = Math.min(row.storageBytes / row.quotaBytes, 1);
  const body = (
    <span className="flex w-28 flex-col items-end gap-1">
      <span className="flex items-center gap-1 font-mono">
        {ofLimit(row.storageBytes, row.quotaBytes)}
        {onEdit && <Pencil className="h-3 w-3 text-muted-foreground" aria-hidden />}
      </span>
      <span className="rail block h-1.5 w-full" aria-hidden>
        <span className="rail-fill block" data-state={railState(fraction)} style={{ width: `${fraction * 100}%` }} />
      </span>
    </span>
  );
  if (!onEdit) return body;
  return (
    <button
      type="button"
      onClick={onEdit}
      aria-label={`${ofLimit(row.storageBytes, row.quotaBytes)}, change storage limit`}
      className="-m-1 rounded-md p-1 hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    >
      {body}
    </button>
  );
}

function StorageLimitDialog({ row, onClose }: { row: TenantRow; onClose: () => void }) {
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
