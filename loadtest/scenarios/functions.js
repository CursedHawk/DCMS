// HTTP functions (ADR 0029): content-api's route, the hop to admin-api, a sandbox per call.
//
//   node loadtest/seed/functions.mjs --env vps1        # once: deploys ping + work in da-stress
//   ./loadtest/run.sh --env vps1 --scenario functions --mode internal --via vps1 --vus 10 --duration 1m
//   ./loadtest/run.sh ... -E FN=work                   # ~50 ms of CPU per call instead of none
//
// Internal mode only. The functions route throttles 120 calls a minute per address, so one load
// generator through the edge measures that limiter within seconds. On the compose network
// content-api trusts X-Forwarded-For, so each request here claims its own visitor address; what
// remains is the per-app cap (3,000 a minute) and the per-tenant concurrency cap (503), and
// those are counted separately rather than hidden in the error rate.

import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import { ramp } from '../lib/k6.js';

const BASE = __ENV.CONTENT_BASE || 'http://content-api:8080';
const TENANT = __ENV.FN_TENANT || 'da-stress';
const APP = __ENV.FN_APP || 'fnload';
const FN = __ENV.FN || 'ping';

const throttled = new Counter('fn_throttled_429');
const busy = new Counter('fn_busy_503');

export const options = {
  scenarios: { functions: ramp() },
  thresholds: {
    // Only answers that are neither a run nor a deliberate refusal count as failures.
    checks: ['rate>0.99'],
    'http_req_duration{expected_response:true}': [`p(95)<${__ENV.FN_P95 || 1000}`],
  },
};

export default function () {
  const address = `10.${(__VU >> 8) & 255}.${__VU & 255}.${__ITER & 255}`;
  const response = http.get(`${BASE}/api/${APP}/fn/${FN}?n=${__ITER}`, {
    headers: { 'X-Dcms-Tenant': TENANT, 'X-Forwarded-For': address },
    tags: { kind: 'function', fn: FN },
    responseCallback: http.expectedStatuses(200),
  });
  if (response.status === 429) throttled.add(1);
  if (response.status === 503) busy.add(1);
  check(response, {
    'ran, or refused by a cap': (r) => r.status === 200 || r.status === 429 || r.status === 503,
  });
}
