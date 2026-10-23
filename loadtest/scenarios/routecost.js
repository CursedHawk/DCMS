// What one request costs the HOST, per route: every route alone, at a fixed rate well below the
// knee, one after another. collect/routecost.mjs then divides each container's CPU-seconds in a
// route's window by the requests it served -- CPU-ms per request in edge, the service, Postgres,
// Redis, Alloy and Tempo.
//
// Why a separate scenario: the staircases measure latency at saturation, and at saturation every
// request is slow because it queues (2026-10-23: an audit read spent 824 ms in admin-api of which
// 158 ms was SQL). That says the host is full, not which request filled it. Cost per request does.
//
//   ./loadtest/run.sh --env vps1 --scenario routecost --via vps1
//   node loadtest/collect/routecost.mjs loadtest/runs/<run>
//
// RC_RATE (req/s, default 30), RC_SEG (s per route, 60), RC_GAP (s between, 20), RC_ONLY (comma list).
// The first RC_SEG seconds run nothing: that window is the idle baseline subtracted from the rest.

import http from 'k6/http';
import { check } from 'k6';
import { SharedArray } from 'k6/data';
import { adminHeaders, deliveryTarget, pickTenant, requireSession, siteTarget, tenants, writeKey } from '../lib/k6.js';

const ADMIN = __ENV.ADMIN_BASE;
const RATE = Number(__ENV.RC_RATE || 30);
const SEG = Number(__ENV.RC_SEG || 60);
const GAP = Number(__ENV.RC_GAP || 20);
// Optional: Dynamic Apps routes run only when its fixture exists (seed/dynamic-apps.mjs).
const DA = new SharedArray('da', () => {
  try { return [JSON.parse(open(`../fixtures/da-${__ENV.PROFILE || 'local'}.json`))]; } catch { return []; }
})[0];

const ok = (r, name, ...statuses) => check(r, { [name]: (x) => (statuses.length ? statuses : [200]).includes(x.status) });
const t = () => pickTenant(__ITER);
const del = (path, opts = {}) => { const { base, headers } = deliveryTarget(t()); return http.get(`${base}${path(t())}`, { headers, ...opts }); };
const adm = (path) => http.get(`${ADMIN}${path(t())}`, { headers: adminHeaders(t()) });
const daH = () => ({ ...adminHeaders({ slug: DA.tenant }), 'Content-Type': 'application/json' });

// [name, rate multiplier, request]. Writes run at a fifth of the rate: each one fans out into
// outbox, audit, NATS and cache invalidation, which is the cost being measured.
const ROUTES = [
  ['delivery_openapi', 1, () => ok(del(() => '/api/openapi.json'), 'openapi')],
  ['delivery_list', 1, () => ok(del((x) => `/api/${x.instanceSlug}/${x.contentType}`), 'list')],
  ['delivery_item', 1, () => ok(del((x) => `/api/${x.instanceSlug}/${x.contentType}/${x.itemSlugs[__ITER % x.itemSlugs.length]}`), 'item')],
  ['delivery_tags', 1, () => ok(del(() => '/api/tags'), 'tags')],
  ['delivery_media', 1, () => ok(del((x) => `/api/media/${x.mediaIds[__ITER % x.mediaIds.length]}/webp-640`,
    { responseCallback: http.expectedStatuses(200, 404) }), 'media', 200, 404)],
  ['site_page', 1, () => { const { base, headers } = siteTarget(t()); ok(http.get(`${base}/page-${__ITER % t().site.pages}`, { headers }), 'page'); }],
  ['site_asset', 1, () => { const { base, headers } = siteTarget(t()); ok(http.get(`${base}/img/large.png`, { headers }), 'asset'); }],
  ['analytics_collect', 1, () => { const { base, headers } = siteTarget(t()); ok(http.post(`${base}/api/collect`,
    JSON.stringify({ type: 'pageview', path: `/page-${__ITER % 20}`, sessionId: `rc-${__VU}-${Math.floor(__ITER / 5)}` }),
    { headers: { ...headers, 'Content-Type': 'application/json', 'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) routecost' } }), 'collect', 202); }],
  ['admin_permissions', 1, () => ok(adm(() => '/api/admin/me/permissions'), 'permissions')],
  ['admin_instances', 1, () => ok(adm(() => '/api/admin/plugins/instances'), 'instances')],
  ['admin_content_page', 1, () => ok(adm((x) => `/api/admin/content/page?instanceId=${x.instanceId}&contentType=${x.contentType}`), 'content')],
  ['admin_picker', 1, () => ok(adm((x) => `/api/admin/content?instanceId=${x.instanceId}&contentType=${x.contentType}&includeDraft=true`), 'picker')],
  ['admin_media', 1, () => ok(adm(() => '/api/admin/media'), 'media')],
  ['admin_sites', 1, () => ok(adm(() => '/api/admin/sites'), 'sites')],
  ['admin_audit', 1, () => ok(adm(() => '/api/admin/audit?limit=50'), 'audit')],
  ['admin_notifications', 1, () => ok(adm(() => '/api/admin/notifications'), 'notifications')],
  ['admin_analytics', 0.2, () => ok(adm(() => '/api/admin/analytics?days=1'), 'analytics')],
  ['admin_publish', 0.2, () => {
    const x = t(); const h = { ...adminHeaders(x), 'Content-Type': 'application/json' };
    const c = http.post(`${ADMIN}/api/admin/content`, JSON.stringify({ pluginInstanceId: x.instanceId, contentType: x.contentType,
      slug: `rc-${__VU}-${__ITER}-${Date.now()}`, data: { title: 'routecost', body: '<p>rc</p>', tags: ['loadtest'] } }),
    { headers: { ...h, ...writeKey() } });
    if (ok(c, 'create', 200, 201)) ok(http.post(`${ADMIN}/api/admin/content/${c.json('id')}/publish`, '{}', { headers: { ...h, ...writeKey() } }), 'publish', 200, 202);
  }],
  ...(DA ? [
    ['da_list', 1, () => ok(http.get(`${ADMIN}/api/admin/plugins/${DA.instanceSlug}/_records/deals?pageSize=50&page=${1 + (__ITER % 50)}&sort=-amount`, { headers: daH() }), 'da list')],
    ['da_get_expand', 1, () => ok(http.get(`${ADMIN}/api/admin/plugins/${DA.instanceSlug}/_records/deals/${DA.ids.deals[__ITER % DA.ids.deals.length]}?expand=company,primary_contact`, { headers: daH() }), 'da get')],
    ['da_site_anon', 1, () => ok(http.get(`${DA.siteBase}/api/${DA.instanceSlug}/data/products?pageSize=50&sort=name`), 'da site')],
  ] : []),
].filter(([name]) => !__ENV.RC_ONLY || __ENV.RC_ONLY.split(',').includes(name));

export const PLAN = ROUTES.map(([name, mult], i) => ({ name, rate: Math.max(1, Math.round(RATE * mult)), start: SEG + i * (SEG + GAP), seconds: SEG }));

export const options = {
  scenarios: Object.fromEntries(PLAN.map((p) => [p.name, {
    executor: 'constant-arrival-rate', exec: 'route', rate: p.rate, timeUnit: '1s', duration: `${p.seconds}s`,
    startTime: `${p.start}s`, preAllocatedVUs: 20, maxVUs: 200, gracefulStop: '5s', env: { RC_ROUTE: p.name },
  }])),
  // No latency gates: this run measures cost, and a slow route is a finding, not a failure.
  thresholds: { http_req_failed: ['rate<0.05'] },
};

const BY_NAME = Object.fromEntries(ROUTES.map(([name, , fn]) => [name, fn]));

export function setup() {
  requireSession();
  if (!tenants.length) throw new Error('routecost needs the fixture estate: node loadtest/seed/provision.mjs');
  // The analyser rebuilds every window from these two lines in k6.log.
  console.log(`ROUTECOST_PLAN ${JSON.stringify(PLAN)}`);
  console.log(`ROUTECOST_T0 ${Date.now()}`);
}

export function route() { BY_NAME[__ENV.RC_ROUTE](); }
