// The publish pipeline, measured end to end: how long after an author clicks Publish does
// a reader actually see the change.
//
// That number crosses four components and is not visible in any single one of them:
//
//   admin-api    writes the content version and a content.published outbox row in the
//                same transaction
//   OutboxDispatcher  polls that table and relays the row to NATS -- a poller, so its
//                interval is a floor on the whole measurement
//   content-api  the ContentCacheInvalidator consumes content.published, deletes the item
//                key and bumps the per-instance generation counter
//   delivery     the next read misses and repopulates
//
// So this scenario publishes, then polls delivery until the new item appears, and records
// the gap as a custom trend. p95 of that trend is the platform's real publish latency;
// dcms_outbox_lag and dcms_messages_handling_duration in the bundle say which of the four
// stages owns it.
//
// Deliberately low-VU. This is a write path with a fan-out behind it; running it at 50 VUs
// would measure how fast the harness can fill a queue, which is not a useful question.

import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import { Trend, Counter } from 'k6/metrics';
import { deliveryTarget, pickTenant, thresholds } from '../lib/k6.js';

const BASE = __ENV.ADMIN_BASE;
const TOKEN = __ENV.ACCESS_TOKEN;

const publishToVisible = new Trend('publish_to_visible_ms', true);
const neverAppeared = new Counter('publish_never_visible');

const POLL_MS = 250;
const GIVE_UP_MS = Number(__ENV.PUBLISH_TIMEOUT_MS || 30000);

export const options = {
  scenarios: {
    publish: {
      executor: 'constant-vus',
      vus: Number(__ENV.VUS || 3),
      duration: __ENV.DURATION || '2m',
    },
  },
  thresholds: thresholds({
    'publish_to_visible_ms': [`p(95)<${__ENV.PUBLISH_P95_MS || 10000}`],
    // Anything that never became visible is a correctness problem, not a slow one.
    'publish_never_visible': ['count==0'],
  }),
};

export function setup() {
  if (!TOKEN) fail('ACCESS_TOKEN is empty; run this through loadtest/run.sh');
}

export default function () {
  const tenant = pickTenant(__ITER);
  const adminHeaders = {
    Authorization: `Bearer ${TOKEN}`,
    'X-Dcms-Tenant': tenant.slug,
    'Content-Type': 'application/json',
  };
  // Unique per VU and iteration so two VUs never contend for one slug, and so a slug is
  // never reused across iterations -- a reused slug could be served from a cache entry the
  // previous iteration warmed, and would report a publish latency of zero.
  const slug = `pub-${__VU}-${__ITER}-${Date.now()}`;

  const created = http.post(`${BASE}/api/admin/content`, JSON.stringify({
    pluginInstanceId: tenant.instanceId,
    contentType: tenant.contentType,
    slug,
    data: { title: `Publish probe ${slug}`, body: '<p>probe</p>', tags: ['loadtest'] },
  }), { headers: adminHeaders, tags: { step: 'create' } });

  if (!check(created, { 'create 200/201': (r) => r.status === 200 || r.status === 201 })) return;
  const id = created.json('id');

  const publishedAt = Date.now();
  const published = http.post(`${BASE}/api/admin/content/${id}/publish`, '{}',
    { headers: adminHeaders, tags: { step: 'publish' } });
  if (!check(published, { 'publish 200/202': (r) => r.status === 200 || r.status === 202 })) return;

  // Poll delivery until the item is servable. Tagged separately so these reads do not
  // pollute the delivery scenario's latency numbers.
  const { base, headers } = deliveryTarget(tenant);
  const url = `${base}/api/${tenant.instanceSlug}/${tenant.contentType}/${slug}`;

  for (;;) {
    const elapsed = Date.now() - publishedAt;
    if (elapsed > GIVE_UP_MS) {
      neverAppeared.add(1);
      break;
    }
    const read = http.get(url, { headers, tags: { step: 'poll' } });
    if (read.status === 200) {
      publishToVisible.add(Date.now() - publishedAt);
      break;
    }
    // sleep() takes seconds and accepts fractions. It yields the VU rather than spinning:
    // a busy-wait would burn load-generator CPU and, on an internal-mode run, that CPU is
    // taken from the box being measured.
    sleep(POLL_MS / 1000);
  }
}
