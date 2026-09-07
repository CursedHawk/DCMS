import { AlertTriangle, ExternalLink } from 'lucide-react';
import { Link } from '@tanstack/react-router';
import { Button, CenteredSpinner } from '@dcms/ui';
import { Rail } from '../../components/Rail';
import { count, plural, since } from '../../lib/format';
import { useTenants } from '../tenants/api';
import { runtimeConfig } from '../../runtime-config';
import { useGrowth, useOverview } from './api';
import { Sparkline } from './Sparkline';

/**
 * The console's landing page.
 *
 * <p>Deliberately not a grid of KPI tiles. "42 tenants" in 48px type is the default treatment
 * for a page like this and it answers no question an operator actually has — they open this
 * console because something is wrong, or because they are about to change something and want
 * to know what they are changing it on. So it opens with a sentence about the platform's
 * state, then the capacity rails, and only then the inventory.</p>
 */
export function OverviewPage() {
  const overview = useOverview();
  const growth = useGrowth(90);
  // Reused rather than a second endpoint: "who is using the disk" is the question the storage
  // section exists to answer, and the tenant directory already carries per-tenant bytes.
  const tenants = useTenants('');

  if (overview.isLoading) return <CenteredSpinner />;

  if (overview.isError) {
    return (
      <Shell>
        <Problem
          title="The platform read model is not answering"
          detail="platform-api could not reach the obs.* reporting views. Check that admin-api has started at least once since the last deploy — it is what creates them."
          onRetry={() => void overview.refetch()}
        />
      </Shell>
    );
  }

  const d = overview.data!;
  const suspended = d.suspendedTenants > 0;
  const series = growth.data ?? [];

  // Only tenants that actually hold bytes: a rail at zero says nothing, and five of them say
  // it five times.
  const topTenants = [...(tenants.data ?? [])]
    .filter((x) => x.storageBytes > 0)
    .sort((a, b) => b.storageBytes - a.storageBytes)
    .slice(0, 5);
  const largest = topTenants[0]?.storageBytes ?? 0;

  return (
    <Shell>
      {/* The status sentence. One line, plain language, and the only large type on the page. */}
      <section className="mb-8">
        <h1 className="text-2xl font-semibold tracking-tight">
          {plural(d.activeTenants, 'tenant')} running
          {suspended && (
            <>
              {', '}
              {/* A suspended tenant is the one thing on this page an operator is likely to want
                  to act on, so the count is the way to the page that acts. */}
              <Link
                to="/tenants"
                className="underline decoration-[hsl(var(--warning))] decoration-2 underline-offset-4 hover:text-[hsl(var(--warning))]"
              >
                {count(d.suspendedTenants)} suspended
              </Link>
            </>
          )}
        </h1>
        <p className="mt-1 text-sm text-muted-foreground">
          {plural(d.members, 'workspace membership')} · {plural(d.sites, 'site')} ·{' '}
          {count(d.publishedItems)} of {plural(d.contentItems, 'content item')} published ·
          refreshed {since(overview.dataUpdatedAt ? new Date(overview.dataUpdatedAt).toISOString() : null)}
        </p>
      </section>

      {/*
        Storage is the one figure here that has a real ceiling today: the box is 72 GB and has
        no swap. The rails for Loki, Prometheus and Tempo belong here too and land with the ops
        endpoints; until then, showing an invented budget would be worse than showing none.
      */}
      <Section title="Storage">
        <Rail
          label="Tenant media"
          used={d.storageBytes}
          limit={PLATFORM_MEDIA_BUDGET_BYTES}
          window="originals and variants, all tenants"
          hint={`${count(d.mediaAssets)} assets`}
        />
        {topTenants.length > 0 && (
          <div className="mt-5 flex flex-col gap-2.5">
            {topTenants.map((tenant) => (
              <Rail
                key={tenant.tenantId}
                label={tenant.slug}
                used={tenant.storageBytes}
                limit={largest}
                window={`${count(tenant.mediaAssets)} assets`}
              />
            ))}
            <p className="text-xs text-muted-foreground">
              The busiest tenants, each against the largest — so the shape of the distribution is
              readable even when the platform total is small.
            </p>
          </div>
        )}

        <p className="mt-4 text-xs text-muted-foreground">
          Telemetry store budgets (Loki, Prometheus, Tempo) appear here once the ops endpoints
          land. Until then{' '}
          <GrafanaLink dashboard="dcms-retention" slug="retention-and-disk">
            Grafana&nbsp;·&nbsp;Retention and disk
          </GrafanaLink>{' '}
          has them.
        </p>
      </Section>

      <Section title="Last 90 days">
        {growth.isError ? (
          <p className="text-sm text-muted-foreground">Growth series unavailable.</p>
        ) : (
          <div className="grid gap-x-8 gap-y-5 sm:grid-cols-2">
            <Trend label="New tenants" series={series.map((p) => p.newTenants)} />
            <Trend label="New users" series={series.map((p) => p.newUsers)} />
            <Trend label="New sites" series={series.map((p) => p.newSites)} />
            <Trend label="New content" series={series.map((p) => p.newContent)} />
          </div>
        )}
      </Section>

    </Shell>
  );
}

/**
 * A nominal ceiling for tenant media so the rail has a scale to read against.
 *
 * Named as a constant rather than hidden in the markup because it IS a guess: the platform has
 * no per-tenant quota and no storage cap in code today. 40 GB is the free space on vps1 that
 * the observability budget was sized against, so it is the honest stand-in until a real quota
 * exists — and when one does, this constant is the single place that changes.
 */
const PLATFORM_MEDIA_BUDGET_BYTES = 40 * 1024 ** 3;

function Shell({ children }: { children: React.ReactNode }) {
  return <div className="mx-auto w-full max-w-4xl px-6 py-8">{children}</div>;
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="mb-8 border-t border-border pt-5">
      <h2 className="mb-4 text-sm font-medium text-foreground">{title}</h2>
      {children}
    </section>
  );
}

function Trend({ label, series }: { label: string; series: number[] }) {
  const total = series.reduce((a, b) => a + b, 0);
  return (
    <div>
      <div className="mb-1 flex items-baseline justify-between gap-3">
        <span className="text-sm text-foreground">{label}</span>
        <span className="font-mono text-sm text-muted-foreground">{count(total)}</span>
      </div>
      <Sparkline values={series} label={label} className="h-9 w-full" />
    </div>
  );
}

function GrafanaLink({
  dashboard, slug, children,
}: { dashboard: string; slug: string; children: React.ReactNode }) {
  const base = runtimeConfig.grafanaBase;
  if (!base) return <>{children}</>;
  return (
    <a
      href={`${base}/d/${dashboard}/${slug}`}
      target="_blank"
      rel="noreferrer"
      className="inline-flex items-center gap-1 text-foreground underline underline-offset-4"
    >
      {children}
      <ExternalLink className="h-3 w-3" aria-hidden />
    </a>
  );
}

function Problem({
  title, detail, onRetry,
}: { title: string; detail: string; onRetry: () => void }) {
  return (
    <div className="flex max-w-xl gap-3 rounded-md border border-border bg-card p-4">
      <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-[hsl(var(--warning))]" aria-hidden />
      <div>
        <h2 className="text-sm font-medium">{title}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{detail}</p>
        <Button variant="outline" size="sm" className="mt-3" onClick={onRetry}>Try again</Button>
      </div>
    </div>
  );
}
