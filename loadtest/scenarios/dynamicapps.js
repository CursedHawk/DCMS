// Dynamic Apps under load: the admin record plane and the site's generated API, mixed, against
// the tenant seed/dynamic-apps.mjs builds (fixtures/da-<profile>.json, ~115k records).
//
//   ./loadtest/run.sh --env vps1 --scenario dynamicapps --vus 20 --duration 2m --via vps1
//   ./loadtest/stress.sh --env vps1 --scenario dynamicapps --steps "5 10 20 40 80" -- --via vps1
//
// Each request is tagged by the shape it measures, because the costs differ in kind:
//   da_admin_list     a page of deals sorted by amount: COUNT(*) + ORDER BY over the table's jsonb
//   da_admin_query    a filter through a lookup (company.industry) plus a range, sorted
//   da_admin_get      one record with two lookups expanded
//   da_admin_related  a company's activities (the inverse of a lookup)
//   da_admin_search   free text over searchable fields (strpos, never indexed)
//   da_admin_create / da_admin_update / da_admin_delete   writes, each one outbox event
//   da_site_anon      the public products list, anonymous
//   da_site_rules     deals through row rules (2-hop EXISTS) as a sales user, a wide user, a bypass manager
//   da_site_rules_get one company through the rules
//   da_site_own       a visitor's own tickets
//
// Public WRITES are deliberately absent: the site API allows 120 an hour per address, so from one
// generator they would measure that throttle (the probe does, once) and nothing else.

import http from 'k6/http';
import { check } from 'k6';
import { SharedArray } from 'k6/data';
import { adminHeaders, ramp, requireSession, thresholds } from '../lib/k6.js';

const BASE = __ENV.ADMIN_BASE;
const FX = new SharedArray('da', () => [JSON.parse(open(`../fixtures/${__ENV.DA_FIXTURES || `da-${__ENV.PROFILE || 'local'}`}.json`))])[0];
const A = `${BASE}/api/admin/plugins/${FX.instanceSlug}`;
const SITE = FX.siteBase;
const P95 = __ENV.ADMIN_P95 || 800;
const ENDPOINTS = ['da_admin_list', 'da_admin_query', 'da_admin_get', 'da_admin_related', 'da_admin_search', 'da_admin_create',
  'da_admin_update', 'da_admin_delete', 'da_site_anon', 'da_site_rules', 'da_site_rules_get', 'da_site_own'];

export const options = {
  scenarios: { dynamicapps: ramp() },
  thresholds: thresholds(Object.fromEntries(ENDPOINTS.map((e) => [`http_req_duration{endpoint:${e}}`, [`p(95)<${P95}`]]))),
};

export function setup() { requireSession(); }

const pick = (xs, i) => xs[i % xs.length];
const json = { 'Content-Type': 'application/json' };
const SITE_USERS = FX.users.filter((u) => ['sales01', 'sales03', 'sales08', 'mgr01'].includes(u.key));
const GUEST = FX.users.find((u) => u.key === 'guest01');
const INDUSTRIES = ['software', 'manufacturing', 'retail', 'finance', 'health', 'energy', 'logistics', 'media'];
let created = [];

export default function () {
  const i = __ITER + __VU * 7919;
  const h = adminHeaders({ slug: FX.tenant });
  const ok = (r, name, ...codes) => check(r, { [`${name} ${codes.join('|')}`]: (x) => codes.includes(x.status) });

  // ---- admin reads
  ok(http.get(`${A}/_records/deals?pageSize=50&page=${1 + (i % 50)}&sort=-amount`, { headers: h, tags: { endpoint: 'da_admin_list' } }), 'list', 200);
  ok(http.post(`${A}/_records/deals/query`, JSON.stringify({
    filter: { and: [{ field: 'company.industry', op: 'eq', value: pick(INDUSTRIES, i) }, { field: 'amount', op: 'gte', value: (i % 9) * 10000 }] },
    sort: [{ field: 'close_date', direction: 'desc' }], pageSize: 25,
  }), { headers: { ...h, ...json }, tags: { endpoint: 'da_admin_query' } }), 'query', 200);
  ok(http.get(`${A}/_records/deals/${pick(FX.ids.deals, i)}?expand=company,primary_contact`, { headers: h, tags: { endpoint: 'da_admin_get' } }), 'get', 200);
  ok(http.get(`${A}/_records/companies/${pick(FX.ids.companies, i)}/activities?pageSize=25`, { headers: h, tags: { endpoint: 'da_admin_related' } }), 'related', 200);
  if (i % 3 === 0) {
    ok(http.get(`${A}/_records/contacts?search=Surname0${String(i % 2000).padStart(4, '0')}&pageSize=10`, { headers: h, tags: { endpoint: 'da_admin_search' } }), 'search', 200);
  }

  // ---- admin writes (one in four iterations), each a row event through the outbox
  if (i % 4 === 0) {
    const r = http.post(`${A}/_records/activities`, JSON.stringify({
      subject: `k6 ${__VU}-${__ITER}`, kind: i % 8 === 0 ? 'meeting' : 'call', company: pick(FX.ids.companies, i), done: false,
    }), { headers: { ...h, ...json }, tags: { endpoint: 'da_admin_create' } });
    if (ok(r, 'create', 201)) created.push(r.json('id'));
    ok(http.patch(`${A}/_records/deals/${pick(FX.ids.deals, i + 13)}`, JSON.stringify({ priority: pick(['low', 'normal', 'high'], i) }),
      { headers: { ...h, ...json }, tags: { endpoint: 'da_admin_update' } }), 'update', 200);
  }
  if (i % 8 === 4 && created.length) {
    ok(http.del(`${A}/_records/activities/${created.shift()}`, null, { headers: h, tags: { endpoint: 'da_admin_delete' } }), 'delete', 204, 404);
  }

  // ---- the site API
  ok(http.get(`${SITE}/api/${FX.instanceSlug}/data/products?pageSize=50&sort=name`, { tags: { endpoint: 'da_site_anon' } }), 'site anon', 200);
  const u = pick(SITE_USERS, i);
  const uh = { headers: { Cookie: u.cookie }, tags: { endpoint: 'da_site_rules', user: u.key } };
  ok(http.get(`${SITE}/api/${FX.instanceSlug}/data/deals?pageSize=25&sort=-amount`, uh), 'site rules', 200);
  ok(http.get(`${SITE}/api/${FX.instanceSlug}/data/companies?pageSize=10&search=Company`, { ...uh, tags: { endpoint: 'da_site_rules_get', user: u.key } }), 'site rules get', 200);
  if (i % 2 === 0) {
    ok(http.get(`${SITE}/api/${FX.instanceSlug}/data/tickets?pageSize=10`, { headers: { Cookie: GUEST.cookie }, tags: { endpoint: 'da_site_own' } }), 'site own', 200);
  }
}
