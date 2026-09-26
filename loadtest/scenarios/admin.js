// The admin plane: authenticated admin-api reads, the surface an operator actually waits on.
//
// Every request here is a LIST endpoint, because that is where the suspicion is. A list
// endpoint that is fine at 50 rows and quadratic at 5,000 looks identical on an empty dev
// tenant, so the fixture estate exists to make the difference visible.
//
// Content is TWO routes, tagged apart, because they are two different costs:
//   content  GET /api/admin/content/page -- what the console's collection list calls: one
//            page, filtered and titled on the server (ContentListEndpoints.cs).
//   picker   GET /api/admin/content?includeDraft=true -- every item of a type WITH its
//            draft, deliberately unpaged: reference pickers, the builder preview and the
//            assistant need all of it. Linear in the collection by design.
// Until 2026-09-26 this scenario called only the second and labelled it "content", so the
// admin numbers measured a picker, not the list an operator waits on.
//
// Tagged per endpoint rather than lumped together: "admin-api is slow" is not actionable
// and "GET /api/admin/audit is slow" is.

import http from 'k6/http';
import { check } from 'k6';
import { adminHeaders, pickTenant, ramp, requireSession, thresholds } from '../lib/k6.js';

const BASE = __ENV.ADMIN_BASE;

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
  requireSession();
}

export default function () {
  const tenant = pickTenant(__ITER);
  const headers = adminHeaders(tenant);

  // Cheap and permission-resolving: hits the Redis-cached effective-permission set
  // (perm:{tenantId}:{userId}, 5 min TTL), so a miss here is a Postgres round trip that
  // every other admin request also pays.
  const perms = http.get(`${BASE}/api/admin/me/permissions`, { headers, tags: { endpoint: 'permissions' } });
  check(perms, { 'permissions 200': (r) => r.status === 200 });

  const instances = http.get(`${BASE}/api/admin/plugins/instances`, { headers, tags: { endpoint: 'instances' } });
  check(instances, { 'instances 200': (r) => r.status === 200 });

  // instanceId is required, so these need the fixture's instance id -- which the seeder
  // recorded precisely so these requests could be made.
  if (tenant.instanceId) {
    const q = `instanceId=${tenant.instanceId}&contentType=${tenant.contentType}`;
    const content = http.get(`${BASE}/api/admin/content/page?${q}`, { headers, tags: { endpoint: 'content' } });
    check(content, { 'content 200': (r) => r.status === 200 });
    // One picker per four iterations: an editor opens one far less often than a list.
    if (__ITER % 4 === 0) {
      const picker = http.get(`${BASE}/api/admin/content?${q}&includeDraft=true`, { headers, tags: { endpoint: 'picker' } });
      check(picker, { 'picker 200': (r) => r.status === 200 });
    }
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
