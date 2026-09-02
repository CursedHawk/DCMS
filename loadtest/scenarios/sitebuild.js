// Site builds: republish a site and wait for the build to succeed.
//
// RUN THIS ALONE. SitePublishConsumer runs MaxAckPending=2
// (src/Services/Dcms.SiteBuilder/SitePublishConsumer.cs:71) and a Mode B build gets a
// sandbox container with DCMS_BUILD_CPUS=2 and DCMS_BUILD_MEM=2g (SandboxOptions.cs). Two
// concurrent builds therefore claim four CPUs and four gigabytes -- the entire host. That
// is the hypothesis: not that a build is slow, but that two of them plus anything else is
// more than the box has.
//
// The fixture sites are Mode C (StaticFiles), so a build here is an extract-and-upload of
// an already-staged bundle -- no Forgejo clone, no pnpm install, no sandbox. That is
// deliberate for a first measurement: it establishes the floor cost of the build pipeline
// itself (queue, consumer, MinIO write, activation) without a Node toolchain's variance on
// top. A Mode B variant is the follow-up, once this number is known.
//
// VUs are builds in flight. Above MaxAckPending the extra ones simply queue, which is the
// point: the trend shows queue latency, and dcms_site_build_duration in the bundle shows
// the work itself. The gap between them is the wait.

import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import { Trend, Counter } from 'k6/metrics';
import { pickTenant, thresholds } from '../lib/k6.js';

const BASE = __ENV.ADMIN_BASE;
const TOKEN = __ENV.ACCESS_TOKEN;

const publishToLive = new Trend('site_publish_to_live_ms', true);
const failedBuilds = new Counter('site_build_failed');
const stuck = new Counter('site_build_never_finished');

const GIVE_UP_MS = Number(__ENV.BUILD_TIMEOUT_MS || 300000);

export const options = {
  scenarios: {
    sitebuild: {
      executor: 'constant-vus',
      vus: Number(__ENV.VUS || 2),
      duration: __ENV.DURATION || '5m',
    },
  },
  thresholds: thresholds({
    'site_build_failed': ['count==0'],
    'site_build_never_finished': ['count==0'],
  }),
};

export function setup() {
  if (!TOKEN) fail('ACCESS_TOKEN is empty; run this through loadtest/run.sh');
}

export default function () {
  const tenant = pickTenant(__VU % 1000);   // one site per VU where possible: two VUs
                                            // republishing the same site would serialize
                                            // on that site rather than on the builder.
  const auth = { Authorization: `Bearer ${TOKEN}`, 'X-Dcms-Tenant': tenant.slug };
  const siteId = tenant.site.siteId;

  const startedAt = Date.now();
  const published = http.post(`${BASE}/api/admin/sites/${siteId}/publish`, '{}', {
    headers: { ...auth, 'Content-Type': 'application/json' },
    tags: { step: 'publish' },
  });
  if (!check(published, { 'publish 200/202': (r) => r.status === 200 || r.status === 202 })) return;

  const buildId = published.json('buildId');

  for (;;) {
    if (Date.now() - startedAt > GIVE_UP_MS) { stuck.add(1); break; }

    const builds = http.get(`${BASE}/api/admin/sites/${siteId}/builds?limit=10`, {
      headers: auth, tags: { step: 'poll' },
    });
    if (builds.status !== 200) { sleep(2); continue; }

    const build = (builds.json() || []).find((b) => b.id === buildId);
    if (build?.status === 'Succeeded') { publishToLive.add(Date.now() - startedAt); break; }
    if (build?.status === 'Failed') { failedBuilds.add(1); break; }
    sleep(2);
  }
}
