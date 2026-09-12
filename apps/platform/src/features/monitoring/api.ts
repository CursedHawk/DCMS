import { useQuery } from '@tanstack/react-query';
import { platformApi } from '../../lib/api';

export interface ServiceHealth {
  service: string;
  requestsPerSecond: number;
  errorsPerSecond: number;
  errorRatio: number;
  /** Null when there were no requests to take a p95 of. Never zero: a p95 of nothing is not fast. */
  p95Seconds: number | null;
}

export interface TargetHealth {
  job: string;
  instance: string;
  /** False means Prometheus tried to scrape it and could not. */
  up: boolean;
}

export interface HealthSignals {
  /** False when Prometheus itself did not answer — a different fact from "nothing is happening". */
  reachable: boolean;
  services: ServiceHealth[];
  targets: TargetHealth[];
}

/**
 * The golden signals, straight from Prometheus.
 *
 * <p>This is what the monitoring page can say when Grafana cannot be reached — and it is worth
 * having above the dashboards even when it can, because "which service is erroring" is the
 * first question and framing a dashboard to answer it costs an OIDC round trip.</p>
 */
export function useHealthSignals(enabled = true) {
  return useQuery({
    queryKey: ['platform-health-signals'],
    enabled,
    queryFn: () => platformApi.get<HealthSignals>('/health/signals'),
    // Prometheus's own scrape interval is the floor on how often this can change, and that
    // period is now kept server-side: `PlatformSampleBroadcaster` pushes the `health` tag and
    // this query refetches on it. Same freshness, and the load on Prometheus stops scaling
    // with the number of tabs left open on this page.
  });
}
