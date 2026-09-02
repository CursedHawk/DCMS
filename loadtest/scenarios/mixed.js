// A platform day: read load on the delivery and site planes while authors publish and an
// operator uses the admin UI, all at once.
//
// This is the scenario that finds contention, and contention is the thing single-surface
// runs cannot see. Every component in this platform shares something with another one:
// content-api and admin-api share Postgres and its connection pool; site-host and
// media-worker share MinIO; every service shares four cores and one NATS. A delivery run
// that is comfortably inside its budget on an otherwise idle host says nothing about
// whether it stays there while a build is running.
//
// The ratios are deliberately lopsided, because real traffic is. Visitors reading a site
// outnumber authors publishing by orders of magnitude, so a mix that gave them equal
// weight would report a bottleneck no real deployment would ever reach.
//
// Everything is tagged by surface, so the bundle can be read either as one number or as
// four -- and the useful reading is the second one: which surface degraded FIRST as the
// others came up.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { deliveryTarget, siteTarget, pickTenant, thresholds } from '../lib/k6.js';

const BASE = __ENV.ADMIN_BASE;
const TOKEN = __ENV.ACCESS_TOKEN;
const VUS = Number(__ENV.VUS || 30);
const DURATION = __ENV.DURATION || '5m';

// Visitors browsing sites are most of the load; delivery API reads are next; the admin
// plane is a handful of people. Floors of 1 so a small --vus never zeroes a surface out
// and silently turns this into a different scenario.
const siteVus = Math.max(1, Math.round(VUS * 0.55));
const deliveryVus = Math.max(1, Math.round(VUS * 0.35));
const adminVus = Math.max(1, Math.round(VUS * 0.10));

export const options = {
  scenarios: {
    visitors: { executor: 'constant-vus', vus: siteVus, duration: DURATION, exec: 'visitor' },
    readers: { executor: 'constant-vus', vus: deliveryVus, duration: DURATION, exec: 'reader' },
    operators: { executor: 'constant-vus', vus: adminVus, duration: DURATION, exec: 'operator' },
    // Authors are rate-based rather than VU-based: publishing is an event that happens a
    // few times an hour, not a loop someone runs as fast as they can.
    authors: {
      executor: 'constant-arrival-rate',
      rate: Number(__ENV.PUBLISH_PER_MINUTE || 6),
      timeUnit: '1m',
      duration: DURATION,
      preAllocatedVUs: 2,
      maxVUs: 5,
      exec: 'author',
    },
  },
  thresholds: thresholds({
    'http_req_duration{surface:site}': [`p(95)<${__ENV.SITEHOST_P95 || 400}`],
    'http_req_duration{surface:delivery}': [`p(95)<${__ENV.DELIVERY_P95 || 300}`],
    'http_req_duration{surface:admin}': [`p(95)<${__ENV.ADMIN_P95 || 800}`],
  }),
};

export function visitor() {
  const tenant = pickTenant(__ITER);
  const { base, headers } = siteTarget(tenant);
  check(http.get(`${base}/`, { headers, tags: { surface: 'site', kind: 'index' } }),
    { 'index 200': (r) => r.status === 200 });
  check(http.get(`${base}/page-${__ITER % tenant.site.pages}`, { headers, tags: { surface: 'site', kind: 'page' } }),
    { 'page 200': (r) => r.status === 200 });
  check(http.get(`${base}/img/medium.png`, { headers, tags: { surface: 'site', kind: 'asset' } }),
    { 'asset 200': (r) => r.status === 200 });
  // Think time. Without it every VU is a tight loop, which measures the platform's
  // response to a benchmark rather than to people.
  sleep(1 + Math.random());
}

export function reader() {
  const tenant = pickTenant(__ITER);
  const { base, headers } = deliveryTarget(tenant);
  check(http.get(`${base}/api/${tenant.instanceSlug}/${tenant.contentType}`,
    { headers, tags: { surface: 'delivery', kind: 'list' } }), { 'list 200': (r) => r.status === 200 });
  const slug = tenant.itemSlugs[__ITER % tenant.itemSlugs.length];
  check(http.get(`${base}/api/${tenant.instanceSlug}/${tenant.contentType}/${slug}`,
    { headers, tags: { surface: 'delivery', kind: 'item' } }), { 'item 200': (r) => r.status === 200 });
  sleep(0.5 + Math.random());
}

export function operator() {
  if (!TOKEN) return;
  const tenant = pickTenant(__ITER);
  const headers = { Authorization: `Bearer ${TOKEN}`, 'X-Dcms-Tenant': tenant.slug };
  check(http.get(`${BASE}/api/admin/content?instanceId=${tenant.instanceId}&contentType=${tenant.contentType}`,
    { headers, tags: { surface: 'admin', endpoint: 'content' } }), { 'content 200': (r) => r.status === 200 });
  check(http.get(`${BASE}/api/admin/media`, { headers, tags: { surface: 'admin', endpoint: 'media' } }),
    { 'media 200': (r) => r.status === 200 });
  check(http.get(`${BASE}/api/admin/notifications`, { headers, tags: { surface: 'admin', endpoint: 'notifications' } }),
    { 'notifications 200': (r) => r.status === 200 });
  sleep(2 + Math.random() * 3);
}

export function author() {
  if (!TOKEN) return;
  const tenant = pickTenant(__ITER);
  const headers = {
    Authorization: `Bearer ${TOKEN}`, 'X-Dcms-Tenant': tenant.slug,
    'Content-Type': 'application/json',
  };
  const slug = `mixed-${__VU}-${__ITER}-${Date.now()}`;
  const created = http.post(`${BASE}/api/admin/content`, JSON.stringify({
    pluginInstanceId: tenant.instanceId,
    contentType: tenant.contentType,
    slug,
    data: { title: `Mixed ${slug}`, body: '<p>mixed</p>', tags: ['loadtest'] },
  }), { headers, tags: { surface: 'admin', endpoint: 'create' } });

  if (created.status !== 200 && created.status !== 201) return;

  // The publish is the point: it bumps the instance generation counter, invalidating every
  // cached list for this tenant. The readers above then all miss at once -- which is
  // exactly the cache-stampede question this scenario exists to ask.
  check(http.post(`${BASE}/api/admin/content/${created.json('id')}/publish`, '{}',
    { headers, tags: { surface: 'admin', endpoint: 'publish' } }),
    { 'publish 200/202': (r) => r.status === 200 || r.status === 202 });
}
