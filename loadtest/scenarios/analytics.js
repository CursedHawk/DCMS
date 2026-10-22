// The analytics beacon: anonymous POST /api/collect on a tenant site, as hydrate.js sends it on
// every page view, then the ANALYTICS stream and admin-api's AnalyticsConsumer behind it.
//
// Two claims about src/Plugins/Dcms.Plugins.Analytics to test:
//
//   collect     the beacon answers 202 after an UNCACHED "is analytics enabled" query and a
//               JetStream publish (AnalyticsIngestEndpoints.MapHostRoutes), so each page view
//               costs Postgres a round trip on the request path.
//   ingest      the consumer persists one single-event message at a time, one transaction each
//               (AnalyticsConsumer.PersistAsync), so stored events can fall behind accepted ones.
//               teardown() waits until each tenant's stored count stops moving and reports the
//               drain as analytics_drain_ms; compare analytics_stored with http_reqs{kind:collect}.
//
// A third, slower one: the dashboard counts raw events (total, DISTINCT visitors, DISTINCT
// sessions), so `dashboard` latency grows with what the run stores. One VU polls it throughout.
//
// The fixtures need the analytics plugin enabled (provision.mjs does it). The User-Agent is a
// real browser's: the beacon drops crawlers, and k6's own agent would be stored as nothing.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { siteTarget, pickTenant, ramp, thresholds, tenants, adminHeaders, requireSession } from '../lib/k6.js';

const ADMIN = __ENV.ADMIN_BASE;
const UA = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36';
const drainMs = new Trend('analytics_drain_ms', true);
const stored = new Counter('analytics_stored');

export const options = {
  scenarios: {
    collect: ramp(),
    dashboard: { executor: 'constant-vus', vus: 1, duration: __ENV.DURATION || '1m', exec: 'dashboard' },
  },
  // The backlog can take a while to drain; that time is the measurement, not a hang.
  teardownTimeout: '30m',
  thresholds: thresholds({
    'http_req_duration{kind:collect}': [`p(95)<${__ENV.COLLECT_P95 || 300}`],
    // Also what puts the dashboard's latency into the summary (and stress.sh's line) at all.
    'http_req_duration{kind:dashboard}': ['p(95)<5000'],
  }),
};

function storedEvents(tenant) {
  const r = http.get(`${ADMIN}/api/admin/analytics?days=1`, { headers: adminHeaders(tenant), tags: { kind: 'dashboard' } });
  return r.status === 200 ? r.json('summary.events') : null;
}

export function setup() {
  requireSession();
  return { base: tenants.map((t) => storedEvents(t) ?? 0) };
}

export default function () {
  const tenant = pickTenant(__ITER);
  const { base, headers } = siteTarget(tenant);
  const body = JSON.stringify({
    type: 'pageview',
    path: `/page-${__ITER % 20}${__ITER % 7 === 0 ? '?utm_source=loadtest' : ''}`,
    referrer: __ITER % 3 === 0 ? 'https://www.google.com/' : null,
    // A visitor makes a few page views per session, as a real one does.
    sessionId: `lt-${__VU}-${Math.floor(__ITER / 5)}`,
  });
  const r = http.post(`${base}/api/collect`, body, {
    headers: { ...headers, 'Content-Type': 'application/json', 'User-Agent': UA },
    tags: { kind: 'collect' },
  });
  check(r, { 'collect 202': (x) => x.status === 202 });
}

export function dashboard() {
  storedEvents(tenants[0]);
  sleep(5);
}

export function teardown(data) {
  const start = Date.now();
  let last = tenants.map(() => -1), stable = 0;
  // Stable for 15 s (three polls) means the consumer has nothing left for these tenants.
  while (stable < 3 && Date.now() - start < 29 * 60_000) {
    const now = tenants.map((t) => storedEvents(t) ?? -1);
    stable = now.every((n, i) => n === last[i] && n >= 0) ? stable + 1 : 0;
    last = now;
    sleep(5);
  }
  drainMs.add(Date.now() - start - 15_000);
  stored.add(last.reduce((a, n, i) => a + Math.max(0, n - data.base[i]), 0));
}
