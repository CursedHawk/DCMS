// Phase 12 load smoke for the content-api delivery plane (cached reads).
// Run against a running stack:  k6 run scripts/load-smoke.js
// Override target + tenant:      k6 run -e BASE=http://localhost:5003 -e TENANT=acme -e SLUG=blog scripts/load-smoke.js
//
// Exercises the read-mostly, horizontally-scalable surface: the per-tenant
// OpenAPI doc and a published content list (both Redis-cached). The cached-read
// target is p95 < 30 ms *internal*; over the loopback we assert a lenient
// p95 < 150 ms and <1% errors so the gate is meaningful without being flaky.
import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.BASE || 'http://localhost:5003';
const TENANT = __ENV.TENANT || 'acme';
const SLUG = __ENV.SLUG || 'blog';
const CONTENT_TYPE = __ENV.CONTENT_TYPE || 'post';

const headers = { 'X-Dcms-Tenant': TENANT };

export const options = {
  scenarios: {
    cached_reads: {
      executor: 'ramping-vus',
      startVUs: 5,
      stages: [
        { duration: '20s', target: 50 },
        { duration: '40s', target: 50 },
        { duration: '10s', target: 0 },
      ],
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{kind:openapi}': ['p(95)<150'],
    'http_req_duration{kind:content}': ['p(95)<150'],
  },
};

export default function () {
  const openapi = http.get(`${BASE}/api/openapi.json`, { headers, tags: { kind: 'openapi' } });
  check(openapi, { 'openapi 200/304': (r) => r.status === 200 || r.status === 304 });

  const content = http.get(`${BASE}/api/${SLUG}/${CONTENT_TYPE}`, { headers, tags: { kind: 'content' } });
  check(content, { 'content 200/404': (r) => r.status === 200 || r.status === 404 });
}
