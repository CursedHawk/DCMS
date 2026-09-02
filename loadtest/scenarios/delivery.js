// content-api's public delivery plane: the read-mostly surface the whole platform is
// shaped around, and the one with a stated internal target of p95 < 30 ms.
//
// Four request kinds, tagged separately because they fail differently:
//
//   openapi   the per-tenant spec. Redis-cached with an ETag over a hash of the
//             instance slugs/configs/versions, so a hit is cheap and a miss re-assembles
//             the document from every enabled plugin instance. Sent WITH If-None-Match
//             half the time to exercise the 304 path a real client would take.
//   list      a published content list -- the hot path, cached under a per-instance
//             generation counter.
//   item      a single item by slug. Same cache, far more distinct keys, so this is
//             where a cache that is not actually hitting shows up first.
//   tags      an aggregate over the tenant's content rather than a keyed lookup, which
//             makes it the query most likely to be doing real work per request.
//   media     bytes for a processed variant, proxied from MinIO by content-api. Different
//             in kind from the four above: they are all Redis-cached JSON measured in
//             kilobytes, this one is an object-storage round trip measured in hundreds.
//             It shares the buffering question with site-host, and its own budget below.
//
// NOT covered here: /api/{slug}/search. It needs a Search plugin instance, which the
// seeder does not create -- adding it is the obvious next extension, and pretending it is
// covered would be worse than saying so.

import http from 'k6/http';
import { check } from 'k6';
import { deliveryTarget, pickTenant, ramp, thresholds } from '../lib/k6.js';

export const options = {
  scenarios: { delivery: ramp() },
  thresholds: thresholds({
    'http_req_duration{kind:openapi}': [`p(95)<${__ENV.DELIVERY_P95 || 300}`],
    'http_req_duration{kind:list}': [`p(95)<${__ENV.DELIVERY_P95 || 300}`],
    'http_req_duration{kind:item}': [`p(95)<${__ENV.DELIVERY_P95 || 300}`],
    'http_req_duration{kind:tags}': [`p(95)<${__ENV.DELIVERY_P95 || 300}`],
    // Bytes from object storage, not cached JSON: held to the site-host asset budget
    // instead, or the run would fail for transferring an image rather than for being slow.
    'http_req_duration{kind:media}': [`p(95)<${__ENV.SITEHOST_ASSET_P95 || 1500}`],
  }),
};

// Held across iterations so the second and later requests can present the ETag the
// first one returned, which is what a browser or an AI client would do.
let etag = null;

export default function () {
  const tenant = pickTenant(__ITER);
  const { base, headers } = deliveryTarget(tenant);

  const openapi = http.get(`${base}/api/openapi.json`, {
    headers: etag ? { ...headers, 'If-None-Match': etag } : headers,
    tags: { kind: 'openapi' },
  });
  check(openapi, { 'openapi 200/304': (r) => r.status === 200 || r.status === 304 });
  if (openapi.status === 200 && openapi.headers.Etag) etag = openapi.headers.Etag;

  const list = http.get(`${base}/api/${tenant.instanceSlug}/${tenant.contentType}`, {
    headers, tags: { kind: 'list' },
  });
  check(list, { 'list 200': (r) => r.status === 200 });

  // Spread across the whole fixture set rather than hammering one slug: a single hot key
  // is served from one cache entry and would report a cache that works even if it does not.
  const slug = tenant.itemSlugs[__ITER % tenant.itemSlugs.length];
  const item = http.get(`${base}/api/${tenant.instanceSlug}/${tenant.contentType}/${slug}`, {
    headers, tags: { kind: 'item' },
  });
  check(item, { 'item 200': (r) => r.status === 200 });

  const tags = http.get(`${base}/api/tags`, { headers, tags: { kind: 'tags' } });
  check(tags, { 'tags 200': (r) => r.status === 200 });

  // Served with immutable cache headers, so a real client fetches each variant once. The
  // interest is what content-api costs on a MISS, which is what every iteration here is.
  if (tenant.mediaIds && tenant.mediaIds.length > 0) {
    const assetId = tenant.mediaIds[__ITER % tenant.mediaIds.length];
    // 404 is a legitimate answer here: the webp ladder skips rungs above the source
    // width, so a 240px fixture has no webp-640 variant. The check below allowed for
    // that, but k6's BUILT-IN http_req_failed does not consult checks -- it counts any
    // non-2xx -- so a correctly-working ladder was reporting as a 4% error rate and
    // failing the run's error threshold. responseCallback is what actually teaches k6
    // which statuses are expected for this one request.
    const media = http.get(`${base}/api/media/${assetId}/webp-640`, {
      headers,
      tags: { kind: 'media' },
      responseCallback: http.expectedStatuses(200, 404),
    });
    check(media, { 'media 200/404': (r) => r.status === 200 || r.status === 404 });
  }
}
