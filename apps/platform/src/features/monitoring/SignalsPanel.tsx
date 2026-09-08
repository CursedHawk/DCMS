import { AlertTriangle, ChevronDown, ChevronRight } from 'lucide-react';
import { useState } from 'react';
import { cn } from '@dcms/ui';
import { plural } from '../../lib/format';
import { useHealthSignals, type ServiceHealth } from './api';

/**
 * The golden signals, above the dashboards, from the console's own API.
 *
 * <p><b>A table, not a chart.</b> The question this answers is "which service is erroring and
 * how slow is it" — an identity question over eight rows, where a reader needs the number
 * itself and the ability to compare two of them exactly. Every chart form makes that worse. The
 * shape of a rate over time is what Grafana is framed below for.</p>
 *
 * <p>Its whole reason for existing is the case where the frame below is empty: Grafana is a
 * container like any other, and an operations console whose only answer when it is down is a
 * blank rectangle has failed at the one moment it exists for.</p>
 */
export function SignalsPanel() {
  const signals = useHealthSignals();
  const [open, setOpen] = useState(true);

  const data = signals.data;
  const down = data?.targets.filter((t) => !t.up) ?? [];

  return (
    <section className="border-b border-border">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        className="flex w-full items-center gap-2 px-4 py-2 text-left hover:bg-accent/40"
      >
        {open ? (
          <ChevronDown className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
        ) : (
          <ChevronRight className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
        )}
        <h2 className="text-sm font-medium">Right now</h2>
        <Summary
          loading={signals.isLoading}
          reachable={data?.reachable ?? true}
          services={data?.services ?? []}
          downCount={down.length}
        />
      </button>

      {open && (
        <div className="px-4 pb-3">
          {/*
            Prometheus not answering is reported as itself. A query that matches nothing and a
            store that never replied both produce an empty table, and telling an operator the
            platform is idle when the metrics pipeline is down sends them to debug the wrong
            thing.
          */}
          {data && !data.reachable ? (
            <Notice>
              Prometheus is not answering, so there are no signals to show. That is itself worth
              acting on: nothing else on this page is being measured either.
            </Notice>
          ) : data && data.services.length === 0 && !signals.isLoading ? (
            <Notice>
              Prometheus answered with no HTTP series. On a platform that is serving traffic this
              means the metrics pipeline is broken rather than that nothing is happening.
            </Notice>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[32rem] text-sm">
                <caption className="sr-only">Request rate, error ratio and latency by service</caption>
                <thead>
                  <tr className="border-b border-border text-xs text-muted-foreground">
                    <th scope="col" className="py-1.5 text-left font-medium">Service</th>
                    <th scope="col" className="py-1.5 text-right font-medium">Requests/s</th>
                    <th scope="col" className="py-1.5 text-right font-medium">Errors</th>
                    <th scope="col" className="py-1.5 text-right font-medium">p95</th>
                  </tr>
                </thead>
                <tbody>
                  {(data?.services ?? []).map((s) => (
                    <Row key={s.service} service={s} />
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {down.length > 0 && (
            <Notice>
              <span className="font-medium">
                {plural(down.length, 'scrape target')} {down.length === 1 ? 'is' : 'are'} down:
              </span>{' '}
              {down.map((t) => `${t.job} (${t.instance})`).join(', ')}
            </Notice>
          )}
        </div>
      )}
    </section>
  );
}

function Row({ service }: { service: ServiceHealth }) {
  // Anything above one in a hundred is worth a colour; the threshold matches the platform's
  // own DcmsHighErrorRatio alert, so the console and the pager cannot disagree about what bad
  // looks like.
  const bad = service.errorRatio > 0.05;
  const warn = !bad && service.errorRatio > 0.01;

  return (
    <tr className="border-b border-border/60 last:border-0">
      <td className="py-1.5 font-medium">{service.service}</td>
      <td className="py-1.5 text-right tabular-nums text-muted-foreground">
        {service.requestsPerSecond.toFixed(2)}
      </td>
      <td
        className={cn(
          'py-1.5 text-right tabular-nums',
          bad && 'font-medium text-destructive',
          warn && 'text-[hsl(var(--warning))]',
          !bad && !warn && 'text-muted-foreground',
        )}
      >
        {/* The ratio, not the count: "0.3 errors per second" means nothing without the traffic
            it happened in, and the ratio is what the alert fires on. */}
        {service.requestsPerSecond > 0 ? `${(service.errorRatio * 100).toFixed(1)}%` : '—'}
      </td>
      <td className="py-1.5 text-right tabular-nums text-muted-foreground">
        {/* Null rather than zero when there were no requests. A p95 of nothing is not fast. */}
        {service.p95Seconds == null ? '—' : formatSeconds(service.p95Seconds)}
      </td>
    </tr>
  );
}

function Summary({
  loading,
  reachable,
  services,
  downCount,
}: {
  loading: boolean;
  reachable: boolean;
  services: ServiceHealth[];
  downCount: number;
}) {
  if (loading) return <span className="ml-auto text-xs text-muted-foreground">Loading</span>;

  const erroring = services.filter((s) => s.errorRatio > 0.05).length;
  const trouble = !reachable || downCount > 0 || erroring > 0;

  return (
    <span
      className={cn(
        'ml-auto flex items-center gap-1.5 text-xs',
        trouble ? 'text-destructive' : 'text-muted-foreground',
      )}
    >
      {trouble && <AlertTriangle className="h-3.5 w-3.5" aria-hidden />}
      {!reachable
        ? 'Prometheus unreachable'
        : downCount > 0
          ? `${plural(downCount, 'target')} down`
          : erroring > 0
            ? `${plural(erroring, 'service')} erroring`
            : `${plural(services.length, 'service')} healthy`}
    </span>
  );
}

function Notice({ children }: { children: React.ReactNode }) {
  return (
    <p className="mt-2 rounded-md border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
      {children}
    </p>
  );
}

/** Milliseconds below a second, because 0.042 s is a number nobody reads at a glance. */
function formatSeconds(seconds: number): string {
  return seconds < 1 ? `${Math.round(seconds * 1000)} ms` : `${seconds.toFixed(2)} s`;
}
