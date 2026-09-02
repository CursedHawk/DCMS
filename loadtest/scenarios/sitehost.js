// Hosted sites: site-host serving a tenant's built artifacts out of MinIO.
//
// This scenario exists to test one specific claim about
// src/Services/Dcms.SiteHost/SiteHostEndpoints.cs -- that every request fetches the
// object from MinIO and copies it whole into a byte[], with no cache, no ETag and no 304,
// and that a page-route miss costs more than one round trip. If that is what is happening,
// these four request kinds separate cleanly:
//
//   index    one small artifact, the same key every time. If THIS does not get faster
//            under sustained load, nothing is caching between site-host and MinIO.
//   page     a named page: the "exact candidate" -- one round trip.
//   detail   /events/<random>: misses the exact candidate, then resolves the events_@.html
//            wildcard. TWO round trips for one request, by design of CandidatesFor.
//   asset    a ~1 MB image. Same code path as index, three orders of magnitude more bytes
//            through the same buffering copy -- so the gap between them measures the copy.
//
// The 404 kind is deliberately absent: a missing asset is the worst case (exact, wildcard,
// then the index.html fallback is SKIPPED for extensioned paths) and would inflate the
// error rate, so it is measured as its own tagged check rather than as failure.

import http from 'k6/http';
import { check } from 'k6';
import { siteTarget, pickTenant, ramp, thresholds } from '../lib/k6.js';

export const options = {
  scenarios: { sitehost: ramp() },
  thresholds: thresholds({
    'http_req_duration{kind:index}': [`p(95)<${__ENV.SITEHOST_P95 || 400}`],
    'http_req_duration{kind:page}': [`p(95)<${__ENV.SITEHOST_P95 || 400}`],
    'http_req_duration{kind:detail}': [`p(95)<${__ENV.SITEHOST_P95 || 400}`],
    // Assets carry ~1 MB, so they get their own budget; holding them to the page
    // threshold would fail the run for transferring bytes rather than for being slow.
    'http_req_duration{kind:asset}': [`p(95)<${__ENV.SITEHOST_ASSET_P95 || 1500}`],
  }),
};

export default function () {
  const tenant = pickTenant(__ITER);
  const { base, headers } = siteTarget(tenant);

  const index = http.get(`${base}/`, { headers, tags: { kind: 'index' } });
  check(index, { 'index 200': (r) => r.status === 200 });

  const page = http.get(`${base}/page-${__ITER % tenant.site.pages}`, { headers, tags: { kind: 'page' } });
  check(page, { 'page 200': (r) => r.status === 200 });

  // Every iteration asks for a different event, so the wildcard page is resolved fresh
  // rather than being answered from whatever the last request warmed.
  const detail = http.get(`${base}/events/item-${__ITER}`, { headers, tags: { kind: 'detail' } });
  check(detail, { 'detail 200 via wildcard': (r) => r.status === 200 });

  const asset = http.get(`${base}/img/large.png`, { headers, tags: { kind: 'asset' } });
  check(asset, { 'asset 200': (r) => r.status === 200 });

  const css = http.get(`${base}/style.css`, { headers, tags: { kind: 'static' } });
  check(css, { 'css 200': (r) => r.status === 200 });
}
