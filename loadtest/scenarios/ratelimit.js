// Measures the per-IP rate limiters, deliberately, as its own scenario.
//
// This exists because of a trap. Two fixed-window limiters are keyed on client IP -- the edge
// (1200 / 60 s across every host, src/Services/Dcms.Edge/Protection/EdgeRateLimiting.cs) and
// content-api's own (600 / 60 s, AddDcmsRateLimiting) -- and a load test is by definition one
// IP. Any delivery run above ~10 req/s stops measuring the platform and starts measuring them.
// A measurement campaign therefore exempts the generator's address in the platform console
// (Operations -> Rate limits), which both limiters honour.
//
// An exemption left behind is exactly how a DoS control gets quietly switched off. So this
// scenario reasserts the limit: run it once WITH the exemption (a refusal share near zero says
// the exemption works) and once AFTER removing it (refusals must come back). It has no latency
// threshold -- being refused is the correct behaviour here.
//
//   ./loadtest/run.sh --env vps1 --scenario ratelimit --vus 20 --duration 90s --via vps1
//
// Note content-api's limiter is in-process, so its ceiling is PermitLimit x replica count.

import http from 'k6/http';
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { deliveryTarget, pickTenant, ramp } from '../lib/k6.js';

const admitted = new Counter('ratelimit_admitted');
const refused = new Counter('ratelimit_refused');
const firstRefusalAt = new Trend('ratelimit_first_refusal_request');

export const options = {
  scenarios: { ratelimit: ramp() },
  // No http_req_failed threshold: a 429 is the point, not a failure.
  thresholds: {},
};

let seen = 0;
let reported = false;

export default function () {
  const tenant = pickTenant(__ITER);
  const { base, headers } = deliveryTarget(tenant);

  const response = http.get(`${base}/api/${tenant.instanceSlug}/${tenant.contentType}`, {
    headers, tags: { kind: 'ratelimit' },
  });
  seen++;

  if (response.status === 429) {
    refused.add(1);
    if (!reported) { firstRefusalAt.add(seen); reported = true; }
  } else {
    admitted.add(1);
  }

  check(response, {
    'answered or refused, never 5xx': (r) => r.status === 200 || r.status === 429 || r.status === 404,
  });
}

export function handleSummary(data) {
  const a = data.metrics.ratelimit_admitted?.values?.count ?? 0;
  const r = data.metrics.ratelimit_refused?.values?.count ?? 0;
  const share = a + r > 0 ? ((r / (a + r)) * 100).toFixed(1) : '0.0';
  return {
    stdout: `\nrate limiter: ${a} admitted, ${r} refused (${share}% of ${a + r})\n`
      + `A refusal share near zero at a rate above the limit means this address is exempt\n`
      + `(expected during a campaign) or the limiter is not enforcing -- remove the exemption\n`
      + `in the platform console and run this again: refusals must come back.\n\n`,
    // run.sh bind-mounts the run directory at /out. Defining handleSummary makes k6
    // ignore --summary-export, so the file has to be written here or the bundle loses
    // its summary entirely.
    '/out/k6-summary.json': JSON.stringify(data, null, 2),
  };
}
