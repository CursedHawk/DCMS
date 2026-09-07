import { useState } from 'react';
import { ExternalLink, Search } from 'lucide-react';
import {
  Button, CenteredSpinner, ConfirmDeleteDialog, EmptyState, Input,
  Table, TBody, TD, TH, THead, TR, toastApiError,
} from '@dcms/ui';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { bytes, count, date } from '../../lib/format';
import { can, useMe, Perm } from '../../lib/permissions';
import { runtimeConfig } from '../../runtime-config';
import { useSetTenantStatus, useTenants, type TenantRow } from './api';

export function TenantsPage() {
  const { t } = useTranslation();
  const [search, setSearch] = useState('');
  const [confirming, setConfirming] = useState<TenantRow | null>(null);
  const me = useMe(true);
  const tenants = useTenants(search);
  const setStatus = useSetTenantStatus();

  const mayChange = can(me.data, Perm.TenantsLifecycle);

  return (
    <div className="mx-auto w-full max-w-5xl px-6 py-8">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold tracking-tight">Tenants</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Every workspace on this platform. Suspending one stops its admin access and takes its
          published sites offline.
        </p>
      </header>

      <div className="relative mb-4 max-w-sm">
        <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
        <Input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search by slug or name"
          aria-label="Search tenants"
          className="pl-8"
        />
      </div>

      {tenants.isLoading ? (
        <CenteredSpinner />
      ) : tenants.data && tenants.data.length === 0 ? (
        <EmptyState
          title={search ? 'No tenant matches that' : 'No tenants yet'}
          description={
            search
              ? 'Try part of the slug instead.'
              : 'Tenants are created from the tenant admin, or by an operator with tenant:write.'
          }
        />
      ) : (
        <Table>
          <THead>
            <TR>
              <TH>Tenant</TH>
              <TH>Status</TH>
              <TH className="text-right">Sites</TH>
              <TH className="text-right">Content</TH>
              <TH className="text-right">Storage</TH>
              <TH className="text-right">Members</TH>
              <TH>Created</TH>
              <TH />
            </TR>
          </THead>
          <TBody>
            {tenants.data?.map((row) => (
              <TR key={row.tenantId}>
                <TD>
                  <div className="flex flex-col">
                    <span className="font-mono text-sm">{row.slug}</span>
                    {row.name && <span className="text-xs text-muted-foreground">{row.name}</span>}
                  </div>
                </TD>
                <TD><StatusPill status={row.status} /></TD>
                <TD className="text-right font-mono text-sm">{count(row.sites)}</TD>
                <TD className="text-right font-mono text-sm">
                  {count(row.publishedItems)} / {count(row.contentItems)}
                </TD>
                <TD className="text-right font-mono text-sm">{bytes(row.storageBytes)}</TD>
                <TD className="text-right font-mono text-sm">{count(row.members)}</TD>
                <TD className="text-sm text-muted-foreground">{date(row.createdAt)}</TD>
                <TD>
                  <div className="flex items-center justify-end gap-1">
                    {/*
                      Media and content are managed in the tenant admin, not here. Rather than
                      rebuild those screens against a cross-tenant API, the console hands the
                      operator a link that opens the admin SPA already pointed at this tenant.
                    */}
                    <a
                      href={`${runtimeConfig.adminBase}/media?tenant=${encodeURIComponent(row.slug)}`}
                      target="_blank"
                      rel="noreferrer"
                      className="inline-flex h-8 items-center gap-1.5 rounded-md px-2 text-xs text-muted-foreground hover:bg-accent hover:text-accent-foreground"
                    >
                      Open in admin
                      <ExternalLink className="h-3 w-3" aria-hidden />
                    </a>

                    {mayChange && (
                      row.status === 'Suspended' ? (
                        <Button
                          variant="outline"
                          size="sm"
                          disabled={setStatus.isPending}
                          onClick={() =>
                            setStatus.mutate(
                              { tenantId: row.tenantId, suspend: false },
                              {
                                onSuccess: () => toast.success(`Resumed ${row.slug}`),
                                onError: (e) => toastApiError(e, t),
                              },
                            )
                          }
                        >
                          Resume
                        </Button>
                      ) : (
                        <Button variant="outline" size="sm" onClick={() => setConfirming(row)}>
                          Suspend
                        </Button>
                      )
                    )}
                  </div>
                </TD>
              </TR>
            ))}
          </TBody>
        </Table>
      )}

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
