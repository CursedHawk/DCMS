import { useMemo, useState } from 'react';
import { Link } from '@tanstack/react-router';
import { DataTable, EmptyState, FilterBar, type Column } from '@dcms/ui';
import { bytes, count, date } from '../../lib/format';
import { useTenants, type TenantRow } from './api';
import { OpenInAdmin, StatusPill, StorageCell } from './parts';

/**
 * Every workspace, to find one and compare them. What can be done to a tenant — its storage
 * limit, suspending it — lives on its own page, beside what it holds and who is in it.
 */
export function TenantsPage() {
  const [search, setSearch] = useState('');
  const tenants = useTenants(search);

  const columns: Column<TenantRow>[] = useMemo(
    () => [
      {
        id: 'tenant',
        header: 'Tenant',
        primary: true,
        sortValue: (r) => r.slug,
        cell: (r) => (
          <Link
            to="/tenants/$tenantId"
            params={{ tenantId: r.tenantId }}
            className="flex flex-col rounded-sm hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <span className="font-mono">{r.slug}</span>
            {r.name && <span className="text-xs text-muted-foreground">{r.name}</span>}
          </Link>
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
        srHeader: 'Media storage of its limit',
        align: 'right',
        // By how full, not by bytes: "who is about to hit the limit" is the question here.
        sortValue: (r) => r.storageBytes / r.quotaBytes,
        cell: (r) => <StorageCell row={r} />,
      },
      {
        id: 'data',
        header: 'Data',
        srHeader: 'Everything the tenant stores',
        align: 'right',
        // By bytes: "which tenant costs the most" is the question here.
        sortValue: (r) => r.usageBytes,
        cell: (r) => <span className="whitespace-nowrap font-mono">{r.usageBytes > 0 ? bytes(r.usageBytes) : '—'}</span>,
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
        cell: (r) => <OpenInAdmin slug={r.slug} />,
      },
    ],
    [],
  );

  return (
    <div className="mx-auto w-full max-w-5xl px-4 py-6 sm:px-6 sm:py-8">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold tracking-tight">Tenants</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Every workspace on this platform. Open one to see what it stores, change its storage
          limit or suspend it.
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
    </div>
  );
}
