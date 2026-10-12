import { useState } from 'react';
import { Link, useParams } from '@tanstack/react-router';
import { ArrowLeft, HardDrive, RefreshCw } from 'lucide-react';
import {
  Button, CenteredSpinner, ConfirmDeleteDialog, EmptyState, Meter, UsageBreakdown, toastApiError,
} from '@dcms/ui';
import { ApiError } from '@dcms/core';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { bytes, count, date, ofLimit, since } from '../../lib/format';
import { can, useMe, Perm } from '../../lib/permissions';
import { useRecalculateUsage, useSetTenantStatus, useTenant, useTenantDirectory, type TenantDetail, type TenantRow } from './api';
import { OpenInAdmin, StatusPill, StorageLimitDialog } from './parts';

/**
 * Everything about one tenant, and everything that can be done to it.
 *
 * <p>The list answers "which tenant"; this page answers "what is it": what it holds, what it
 * stores and where, who is in it, and its limit and status. Every number here comes from the
 * obs views or from admin-api by delegation, like the rest of the console.</p>
 */
export function TenantDetailPage() {
  const { tenantId } = useParams({ strict: false }) as { tenantId: string };
  const detail = useTenant(tenantId);

  if (detail.isLoading) return <CenteredSpinner />;
  if (!detail.data) {
    return (
      <div className="mx-auto w-full max-w-5xl px-4 py-6 sm:px-6 sm:py-8">
        <BackLink />
        <EmptyState title="No such tenant" description="It may have been deleted. Pick one from the list." />
      </div>
    );
  }

  const { tenant, originalBytes, variantBytes, usage } = detail.data;
  return (
    <div className="mx-auto w-full max-w-5xl px-4 py-6 sm:px-6 sm:py-8">
      <BackLink />
      <Header tenant={tenant} />

      <Section title="Overview">
        <dl className="grid grid-cols-2 gap-x-6 gap-y-4 sm:grid-cols-4">
          <Fact label="Sites" value={count(tenant.sites)} />
          <Fact label="Content" value={`${count(tenant.publishedItems)} / ${count(tenant.contentItems)}`} hint="published / total" />
          <Fact label="Members" value={count(tenant.members)} />
          <Fact label="Domains" value={`${count(tenant.verifiedDomains)} / ${count(tenant.domains)}`} hint="verified / total" />
          <Fact label="Media files" value={count(tenant.mediaAssets)} />
          <Fact label="Plugins enabled" value={count(tenant.enabledPlugins)} />
          <Fact label="Site visitors" value={count(tenant.visitorAccounts)} hint="registered accounts" />
          <Fact label="Form submissions" value={count(tenant.formSubmissions)} />
        </dl>
      </Section>

      <StorageLimit tenant={tenant} originalBytes={originalBytes} variantBytes={variantBytes} />
      <DataUsage tenantId={tenant.tenantId} usage={usage} />
      <Directory tenantId={tenant.tenantId} />
    </div>
  );
}

function BackLink() {
  return (
    <Link
      to="/tenants"
      className="mb-4 inline-flex items-center gap-1.5 rounded-sm text-sm text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    >
      <ArrowLeft className="h-3.5 w-3.5" aria-hidden />
      Tenants
    </Link>
  );
}

function Header({ tenant }: { tenant: TenantRow }) {
  const { t } = useTranslation();
  const me = useMe(true);
  const setStatus = useSetTenantStatus();
  const [confirming, setConfirming] = useState(false);
  const mayChange = can(me.data, Perm.TenantsLifecycle);
  const suspended = tenant.status === 'Suspended';

  const resume = () =>
    setStatus.mutate(
      { tenantId: tenant.tenantId, suspend: false },
      { onSuccess: () => toast.success(`Resumed ${tenant.slug}`), onError: (e) => toastApiError(e, t) },
    );

  return (
    <header className="mb-8 flex flex-wrap items-start justify-between gap-4">
      <div className="min-w-0">
        <h1 className="text-2xl font-semibold tracking-tight [overflow-wrap:anywhere]">{tenant.name ?? tenant.slug}</h1>
        <p className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-muted-foreground">
          <span className="font-mono">{tenant.slug}</span>
          <StatusPill status={tenant.status} />
          <span>Created {date(tenant.createdAt)}</span>
        </p>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <OpenInAdmin slug={tenant.slug} />
        {mayChange &&
          (suspended ? (
            <Button variant="outline" size="sm" disabled={setStatus.isPending} onClick={resume}>Resume</Button>
          ) : (
            <Button variant="outline" size="sm" onClick={() => setConfirming(true)}>Suspend</Button>
          ))}
      </div>

      {/*
        Type-to-confirm. Suspension is reversible, so this is not the ceremony a deletion gets —
        but it takes a paying tenant's public site offline, which is not something to do by
        mis-clicking.
      */}
      {confirming && (
        <ConfirmDeleteDialog
          open
          onOpenChange={(open) => !open && setConfirming(false)}
          title={`Suspend ${tenant.slug}?`}
          description="Type the tenant's slug to confirm. You can resume it from this page afterwards."
          confirmationValue={tenant.slug}
          confirmLabel="Suspend tenant"
          pending={setStatus.isPending}
          consequences={[
            'Everyone in this workspace loses admin access immediately.',
            `Its published sites stop serving${tenant.sites > 0 ? ` (${count(tenant.sites)} site${tenant.sites === 1 ? '' : 's'})` : ''}.`,
            'Nothing is deleted. You can resume it from this page.',
          ]}
          onConfirm={() =>
            setStatus.mutate(
              { tenantId: tenant.tenantId, suspend: true },
              {
                onSuccess: () => {
                  toast.success(`Suspended ${tenant.slug}`);
                  setConfirming(false);
                },
                onError: (e) => toastApiError(e, t),
              },
            )
          }
        />
      )}
    </header>
  );
}

function StorageLimit({ tenant, originalBytes, variantBytes }: { tenant: TenantRow; originalBytes: number; variantBytes: number }) {
  const me = useMe(true);
  const [editing, setEditing] = useState(false);
  const maySetLimits = can(me.data, Perm.TenantsWrite);

  return (
    <Section
      title="Storage limit"
      action={maySetLimits && <Button variant="outline" size="sm" onClick={() => setEditing(true)}>Change limit</Button>}
    >
      <Meter
        label="Media files"
        value={ofLimit(tenant.storageBytes, tenant.quotaBytes)}
        fraction={tenant.quotaBytes > 0 ? tenant.storageBytes / tenant.quotaBytes : 0}
      />
      <p className="mt-2 text-xs text-muted-foreground">
        Originals {bytes(originalBytes)}, optimized copies {bytes(variantBytes)}. Only media files
        count toward the limit; uploads are refused once it is reached.
      </p>
      {editing && <StorageLimitDialog row={tenant} onClose={() => setEditing(false)} />}
    </Section>
  );
}

function DataUsage({ tenantId, usage }: { tenantId: string; usage: TenantDetail['usage'] }) {
  const { t } = useTranslation();
  const me = useMe(true);
  const recalculate = useRecalculateUsage(tenantId);
  const mayMeasure = can(me.data, Perm.TenantsWrite);

  const measure = () =>
    recalculate.mutate(undefined, {
      onSuccess: () => toast.success('Usage measured'),
      onError: (e) =>
        e instanceof ApiError && e.status === 429
          ? toast.info('Usage was measured moments ago. Try again in a couple of minutes.')
          : toastApiError(e, t),
    });

  const button = mayMeasure && (
    <Button variant="outline" size="sm" onClick={measure} disabled={recalculate.isPending}>
      <RefreshCw className={recalculate.isPending ? 'animate-spin motion-reduce:animate-none' : undefined} />
      Measure now
    </Button>
  );

  return (
    <Section title="Data usage" action={button}>
      {usage?.measuredAt ? (
        <>
          <p className="mb-4 text-sm text-muted-foreground">
            <span className="text-2xl font-semibold text-foreground tabular-nums">{bytes(usage.totalBytes)}</span>{' '}
            stored in total, measured {since(usage.measuredAt)}.
          </p>
          <UsageBreakdown categories={usage.categories} totalBytes={usage.totalBytes} />
        </>
      ) : (
        <EmptyState
          icon={HardDrive}
          title="Not measured yet"
          description="Every tenant is measured once a day. Measure this one now to see what it stores."
        />
      )}
    </Section>
  );
}

function Directory({ tenantId }: { tenantId: string }) {
  const directory = useTenantDirectory(tenantId);
  const d = directory.data;

  return (
    <>
      <Section title="Domains">
        <List
          isLoading={directory.isLoading}
          empty="No domains yet."
          rows={d?.domains.map((x) => ({
            key: x.hostname,
            primary: <span className="font-mono [overflow-wrap:anywhere]">{x.hostname}</span>,
            secondary: [x.primary && 'Primary', x.verified ? 'Verified' : 'Not verified'].filter(Boolean).join(', '),
          }))}
        />
      </Section>
      <Section title="Members">
        <List
          isLoading={directory.isLoading}
          empty="Nobody is in this workspace."
          rows={d?.members.map((x) => ({
            key: x.userId,
            primary: <span className="[overflow-wrap:anywhere]">{x.email}</span>,
            secondary: x.roles.length > 0 ? x.roles.join(', ') : 'No role',
          }))}
        />
      </Section>
      <Section title="Plugins">
        <List
          isLoading={directory.isLoading}
          empty="No plugins installed."
          rows={d?.plugins.map((x) => ({
            key: x.id,
            primary: (
              <span>
                {x.name} <span className="font-mono text-xs text-muted-foreground">{x.pluginId}</span>
              </span>
            ),
            secondary: `/${x.slug}${x.enabled ? '' : ', disabled'}`,
          }))}
        />
      </Section>
    </>
  );
}

function List({
  rows,
  isLoading,
  empty,
}: {
  rows?: { key: string; primary: React.ReactNode; secondary: string }[];
  isLoading: boolean;
  empty: string;
}) {
  if (isLoading) return <p className="text-sm text-muted-foreground">Loading…</p>;
  if (!rows || rows.length === 0) return <p className="text-sm text-muted-foreground">{empty}</p>;
  return (
    <ul className="divide-y rounded-md border">
      {rows.map((r) => (
        <li key={r.key} className="grid grid-cols-1 gap-x-4 gap-y-0.5 px-3 py-2 text-sm sm:grid-cols-[1fr_auto] sm:items-baseline">
          {r.primary}
          <span className="text-xs text-muted-foreground">{r.secondary}</span>
        </li>
      ))}
    </ul>
  );
}

function Section({ title, action, children }: { title: string; action?: React.ReactNode; children: React.ReactNode }) {
  return (
    <section className="mb-8">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-base font-semibold">{title}</h2>
        {action}
      </div>
      {children}
    </section>
  );
}

function Fact({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="font-mono text-lg">{value}</dd>
      {hint && <dd className="text-xs text-muted-foreground">{hint}</dd>}
    </div>
  );
}
