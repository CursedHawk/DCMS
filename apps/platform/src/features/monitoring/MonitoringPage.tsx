import { useState } from 'react';
import { ExternalLink } from 'lucide-react';
import { EmptyState, useTheme } from '@dcms/ui';
import { runtimeConfig } from '../../runtime-config';
import { SignalsPanel } from './SignalsPanel';

/**
 * Grafana's dashboards, embedded.
 *
 * <p>The platform already has 24 dashboards and 277 panels, provisioned from JSON and treated
 * as source. Re-drawing them in this console would be months of work to arrive somewhere
 * worse, so the console frames them and spends its own effort on what Grafana cannot do —
 * acting on what the dashboards show.</p>
 *
 * <p>The frame signs itself in: the edge's OIDC round trip is all redirects (response_mode
 * query), and identity's cookie is same-site with this console, so a signed-in operator lands
 * on the dashboard with no click. If the Grafana origin is not configured, there is nothing to
 * frame and the page says so rather than showing an empty rectangle.</p>
 */
const DASHBOARDS = [
  { uid: 'dcms-overview', slug: 'platform-overview', label: 'Golden signals' },
  { uid: 'dcms-http-traffic', slug: 'http-traffic', label: 'HTTP traffic' },
  { uid: 'dcms-host', slug: 'host', label: 'Host' },
  { uid: 'dcms-containers', slug: 'containers', label: 'Containers' },
  { uid: 'dcms-postgres', slug: 'postgres', label: 'Postgres' },
  { uid: 'dcms-nats', slug: 'nats-jetstream', label: 'NATS' },
  { uid: 'dcms-retention', slug: 'retention-and-disk', label: 'Retention and disk' },
  { uid: 'dcms-tenant-explorer', slug: 'tenant-explorer', label: 'Tenant explorer' },
  { uid: 'dcms-audit-health', slug: 'audit-health', label: 'Audit health' },
  { uid: 'dcms-trace-lookup', slug: 'trace-lookup', label: 'Trace lookup' },
] as const;

export function MonitoringPage() {
  const { resolved } = useTheme();
  const [current, setCurrent] = useState<(typeof DASHBOARDS)[number]>(DASHBOARDS[0]);
  const base = runtimeConfig.grafanaBase.replace(/\/+$/, '');

  if (!base) {
    // Still shows the signals. Grafana being unconfigured is a reason to have less on this
    // page, not a reason for it to be empty — the numbers below come from platform-api.
    return (
      <div className="flex h-full flex-col">
        <SignalsPanel />
        <div className="mx-auto w-full max-w-3xl px-4 sm:px-6 py-16">
          <EmptyState
            title="No Grafana address configured"
            description="Set DCMS_GRAFANA_BASE on the platform-spa container to embed the dashboards here. Everything else in this console works without it."
          />
        </div>
      </div>
    );
  }

  const src =
    `${base}/d/${current.uid}/${current.slug}` +
    `?kiosk&theme=${resolved}&from=now-6h&to=now`;

  return (
    <div className="flex h-full flex-col">
      {/* Above the frame on purpose: "which service is erroring" is the first question, and
          this answers it from platform-api even when Grafana is the thing that is down. */}
      <SignalsPanel />

      <div className="flex flex-wrap items-center gap-2 border-b border-border px-4 py-2">
        <label className="sr-only" htmlFor="dashboard">Dashboard</label>
        <select
          id="dashboard"
          value={current.uid}
          onChange={(e) => setCurrent(DASHBOARDS.find((d) => d.uid === e.target.value) ?? DASHBOARDS[0])}
          className="h-8 rounded-md border border-input bg-background px-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {DASHBOARDS.map((d) => (
            <option key={d.uid} value={d.uid}>{d.label}</option>
          ))}
        </select>

        <a
          href={`${base}/d/${current.uid}/${current.slug}`}
          target="_blank"
          rel="noreferrer"
          className="inline-flex h-8 items-center gap-1.5 rounded-md px-2 text-sm text-muted-foreground hover:bg-accent hover:text-accent-foreground"
        >
          Open in Grafana
          <ExternalLink className="h-3.5 w-3.5" aria-hidden />
        </a>
      </div>

      <iframe
        key={`${current.uid}-${resolved}`}
        src={src}
        title={`Grafana: ${current.label}`}
        className="min-h-0 flex-1 border-0 bg-background"
      />
    </div>
  );
}
