// Measures content-api's per-IP rate limiter, deliberately, as its own scenario.
//
// This exists because of a trap. The limiter is a global fixed window keyed on client IP
// (src/Shared/Dcms.Shared.Hosting/DcmsHostingExtensions.cs:206), default 600 requests per
// 60 seconds, and a load test is by definition one IP. At the default, any delivery run
// above ~10 req/s stops measuring the platform and starts measuring the limiter -- so the
// harness raises RateLimiting__PermitLimit for a measurement run.
//
// Raising a security control for convenience is exactly how one gets quietly left off.
// So this scenario reasserts it: it drives a known rate and reports where 429s begin, and
// it is meant to be run AFTER a campaign, against the limit the environment will actually
// keep. It has no latency threshold -- being refused is the correct behaviour here, and
// the number of interest is the request count at which refusal starts.
//
//   ./loadtest/run.sh --env vps1 --scenario ratelimit --vus 20 --duration 90s
//
// Note the limiter is in-process, so the effective ceiling is PermitLimit x replica count.

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
      + `A refusal share near zero at a rate above the configured limit means the limiter\n`
      + `is not enforcing -- check RateLimiting__PermitLimit was restored after the campaign.\n\n`,
    // run.sh bind-mounts the run directory at /out. Defining handleSummary makes k6
    // ignore --summary-export, so the file has to be written here or the bundle loses
    // its summary entirely.
    '/out/k6-summary.json': JSON.stringify(data, null, 2),
  };
}
