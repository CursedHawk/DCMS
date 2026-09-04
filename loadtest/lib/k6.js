// Shared init-context helpers for the k6 scenarios.
//
// Everything here runs once per VU at init, never in the iteration body: k6 charges the
// iteration for whatever happens inside it, so JSON parsing or URL building in the hot
// loop would show up as latency the platform never caused.

import { SharedArray } from 'k6/data';

/**
 * The fixture estate, parsed once for the whole run rather than once per VU. At 50 VUs
 * the difference is fifty copies of the tenant list resident in memory versus one.
 */
export const tenants = new SharedArray('tenants', () => {
  const profile = __ENV.PROFILE || 'local';
  return JSON.parse(open(`../fixtures/${profile}.json`)).tenants;
});

export const MODE = __ENV.MODE || 'edge';

/**
 * Where a tenant's *delivery* surface lives, which is not the same place in both modes.
 *
 * The edge routes /api on the admin host to admin-api; content delivery is only reachable
 * through a tenant domain, where site-host proxies /api on to content-api and injects the
 * tenant slug (src/Services/Dcms.SiteHost/ApiProxy.cs). So:
 *
 *   edge     -> https://<tenant-domain>/api/...   through the edge and site-host, the real path
 *   internal -> http://content-api:8080/api/...   with the tenant header set by hand
 *
 * The internal form skips both the edge and site-host's proxy hop, which is the point:
 * differencing the two says how much of the latency belongs to each.
 */
export function deliveryTarget(tenant) {
  if (MODE === 'internal') {
    return {
      base: __ENV.CONTENT_BASE || 'http://content-api:8080',
      // The SLUG. TenantStore resolves this header through GetByIdentifierAsync against
      // Tenant.Identifier, so a uuid resolves to no tenant at all -- and several endpoints
      // then dereference a null tenant id and 500 rather than refusing the request.
      headers: { 'X-Dcms-Tenant': tenant.slug },
    };
  }
  return { base: `https://${tenant.site.domain}`, headers: {} };
}

/**
 * Where a tenant's *site* is served from.
 *
 * In internal mode the Host header carries the domain, because that is what
 * DomainResolver actually reads — TLS SNI never reaches site-host, so addressing the
 * container directly and naming the domain in the header is the same request minus the
 * edge. It also means site scenarios need no DNS and no certificate.
 */
export function siteTarget(tenant) {
  if (MODE === 'internal') {
    return {
      base: __ENV.SITEHOST_BASE || 'http://site-host:8080',
      headers: { Host: tenant.site.domain },
    };
  }
  return { base: `https://${tenant.site.domain}`, headers: {} };
}

/** Round-robin rather than random, so every tenant gets the same share of the load. */
export function pickTenant(iteration) {
  return tenants[iteration % tenants.length];
}

const num = (name, fallback) => (__ENV[name] ? Number(__ENV[name]) : fallback);

/**
 * The ramp shape every scenario shares, so two scenarios' numbers are comparable.
 * A short ramp, a long plateau, a short drain: the plateau is the measurement and the
 * ramp exists so connection setup and JIT warm-up do not land inside it.
 */
export function ramp() {
  const vus = num('VUS', 10);
  return {
    executor: 'ramping-vus',
    startVUs: Math.max(1, Math.floor(vus / 10)),
    stages: [
      { duration: __ENV.RAMP || '15s', target: vus },
      { duration: __ENV.DURATION || '1m', target: vus },
      { duration: '10s', target: 0 },
    ],
    gracefulRampDown: '10s',
  };
}

/**
 * Error-rate and latency gates.
 *
 * `http_req_failed` counts only what k6 considers a failed request, so a scenario that
 * expects 404s must mark them passing in its own checks rather than loosening this.
 */
export function thresholds(extra = {}) {
  return {
    http_req_failed: [`rate<${num('ERROR_RATE', 0.01)}`],
    // Not abort-on-fail: a run that stops at the first breach collects no evidence about
    // what it was doing when it breached, and the evidence is the deliverable.
    ...extra,
  };
}
