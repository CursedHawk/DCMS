// The admin plane: authenticated admin-api reads, the surface an operator actually waits on.
//
// Every request here is a LIST endpoint, because that is where the suspicion is. Several
// of them page nothing at all -- GET /api/admin/content builds its projection over every
// row for an instance and serializes the lot (src/Services/Dcms.AdminApi/Cms/
// ContentEndpoints.cs:29), and the media and audit lists are worth checking for the same
// shape. A list endpoint that is fine at 50 rows and quadratic at 5,000 looks identical
// on an empty dev tenant, so the fixture estate exists to make the difference visible.
//
// Tagged per endpoint rather than lumped together: "admin-api is slow" is not actionable
// and "GET /api/admin/audit is slow" is.

import http from 'k6/http';
import { check } from 'k6';
import { pickTenant, ramp, thresholds } from '../lib/k6.js';

const BASE = __ENV.ADMIN_BASE;
const TOKEN = __ENV.ACCESS_TOKEN;

export const options = {
  scenarios: { admin: ramp() },
  thresholds: thresholds({
    'http_req_duration{endpoint:content}': [`p(95)<${__ENV.ADMIN_P95 || 800}`],
    'http_req_duration{endpoint:media}': [`p(95)<${__ENV.ADMIN_P95 || 800}`],
    'http_req_duration{endpoint:audit}': [`p(95)<${__ENV.ADMIN_P95 || 800}`],
    'http_req_duration{endpoint:notifications}': [`p(95)<${__ENV.ADMIN_P95 || 800}`],
  }),
};

export function setup() {
  if (!TOKEN) {
    throw new Error('ACCESS_TOKEN is empty. run.sh mints one for this scenario; '
      + 'running k6 by hand needs: -e ACCESS_TOKEN=$(node loadtest/lib/mint-token.mjs --env <env>)');
  }
}

export default function () {
  const tenant = pickTenant(__ITER);
  const headers = {
    Authorization: `Bearer ${TOKEN}`,
    'X-Dcms-Tenant': tenant.slug,
  };

  // Cheap and permission-resolving: hits the Redis-cached effective-permission set
  // (perm:{tenantId}:{userId}, 5 min TTL), so a miss here is a Postgres round trip that
  // every other admin request also pays.
  const perms = http.get(`${BASE}/api/admin/me/permissions`, { headers, tags: { endpoint: 'permissions' } });
  check(perms, { 'permissions 200': (r) => r.status === 200 });

  const instances = http.get(`${BASE}/api/admin/plugins/instances`, { headers, tags: { endpoint: 'instances' } });
  check(instances, { 'instances 200': (r) => r.status === 200 });

  // The unpaged one. instanceId is required, so this needs the fixture's instance id --
  // which the seeder recorded precisely so this request could be made.
  if (tenant.instanceId) {
    const content = http.get(
      `${BASE}/api/admin/content?instanceId=${tenant.instanceId}&contentType=${tenant.contentType}`,
      { headers, tags: { endpoint: 'content' } });
    check(content, { 'content 200': (r) => r.status === 200 });
  }

  const media = http.get(`${BASE}/api/admin/media`, { headers, tags: { endpoint: 'media' } });
  check(media, { 'media 200': (r) => r.status === 200 });

  const sites = http.get(`${BASE}/api/admin/sites`, { headers, tags: { endpoint: 'sites' } });
  check(sites, { 'sites 200': (r) => r.status === 200 });

  // The audit log is append-only and partitioned, and grows faster than anything else on
  // the platform -- every request above adds to it. Reading it under load is therefore the
  // one list whose cost changes during the run itself.
  const audit = http.get(`${BASE}/api/admin/audit?limit=50`, { headers, tags: { endpoint: 'audit' } });
  check(audit, { 'audit 200': (r) => r.status === 200 });

  const notifications = http.get(`${BASE}/api/admin/notifications`, { headers, tags: { endpoint: 'notifications' } });
  check(notifications, { 'notifications 200': (r) => r.status === 200 });
}
