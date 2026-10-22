#!/usr/bin/env node
// Checks every Dynamic Apps feature in the stress tenant, then pushes each documented limit and
// measures what it costs. Run after seed/dynamic-apps.mjs; writes runs/da-probe-<ts>.json.
//
//   node loadtest/seed/dynamic-apps-probe.mjs --env vps1 [--only functional|limits|automation|scale]
//   node loadtest/seed/dynamic-apps-probe.mjs --env vps1 -e da.tenant=da-smoke
//
// Sections:
//   functional  public API, OpenAPI/client, row rules per user, role bypass, own records, hidden and
//               read-only fields, site flows, page gating, concurrency, drafts, revisions/rollback,
//               relationship delete behaviours, uniqueness, forms -> flows, cascade limit, email
//   limits      every hard cap answered as documented (page size, offset, predicates, change set,
//               import, bulk, flow input, public write throttle)
//   automation  a bulk stage change sets off a storm of chained flows; drain time and runs/s
//   scale       latency of the expensive shapes at full data size: deep pages, counts, row rules
//               narrow vs wide vs bypass, expand, search, publish over data, parallel imports

import { loadProfile, parseArgs, targets } from '../lib/config.mjs';
import { edgeSession, sessionHeaders } from '../lib/auth.mjs';
import { readFixtures } from '../lib/fixtures.mjs';
import { LOADTEST_ROOT } from '../lib/config.mjs';
import { writeFile, mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { pool } from './dynamic-apps.mjs';

const { env, overrides, rest } = parseArgs(process.argv.slice(2));
const only = rest[rest.indexOf('--only') + 1] && rest.includes('--only') ? rest[rest.indexOf('--only') + 1] : null;
const profile = await loadProfile(env, { overrides });
const t = targets(profile);
const tenant = profile.da?.tenant ?? 'da-stress';
const fx = await readFixtures(`da-${profile.name}${tenant === 'da-stress' ? '' : `-${tenant}`}`);
const session = await edgeSession({ admin: t.admin, username: profile.auth.username, password: profile.auth.password });
const adminHeaders = { ...sessionHeaders(session), 'X-Dcms-Tenant': fx.tenant };
const A = '/api/admin/plugins/crm';
const user = Object.fromEntries(fx.users.map((u) => [u.key, u]));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const report = { tenant: fx.tenant, startedAt: new Date().toISOString(), checks: [], measures: {} };
let failed = 0;

/** One check: a name, and a function returning true/false or throwing. Never aborts the run. */
async function check(name, fn) {
  const started = Date.now();
  let ok = false, detail;
  try { const r = await fn(); ok = r === true || (r && r.ok === true); detail = r?.detail ?? (r === true ? undefined : r); } catch (e) { detail = e.message; }
  if (!ok) failed++;
  report.checks.push({ name, ok, ms: Date.now() - started, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : `  -- ${typeof detail === 'string' ? detail : JSON.stringify(detail)?.slice(0, 300)}`}`);
}
const measure = (key, value) => { report.measures[key] = value; console.log(`  measure ${key}: ${JSON.stringify(value)}`); };

async function call(method, path, body, { site = false, as = null, headers = {} } = {}) {
  const url = site ? `${fx.siteBase}${path}` : `${t.admin}${path.startsWith('/api') ? path : A + path}`;
  const h = { ...headers };
  if (!site) Object.assign(h, adminHeaders, method === 'GET' ? {} : { 'Idempotency-Key': crypto.randomUUID() });
  if (as) h.cookie = user[as].cookie;
  if (body !== undefined) { h['content-type'] = 'application/json'; }
  const s = performance.now();
  const res = await fetch(url, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual' });
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { /* html */ }
  return { status: res.status, body: json, text, ms: Math.round(performance.now() - s), headers: res.headers };
}
const site = (method, path, body, as, headers) => call(method, path, body, { site: true, as, headers });
const expect = (r, ...codes) => codes.includes(r.status) ? { ok: true } : { ok: false, detail: `${r.status} ${r.text.slice(0, 200)}` };

/** Median latency of n sequential calls. */
async function timed(n, fn) {
  const xs = [];
  for (let i = 0; i < n; i++) { const r = await fn(); if (r.status >= 400) return { error: `${r.status} ${r.text.slice(0, 150)}` }; xs.push(r.ms); }
  xs.sort((a, b) => a - b);
  return { p50: xs[Math.floor(n / 2)], p90: xs[Math.floor(n * 0.9)], max: xs.at(-1) };
}

async function runs(flow, status) {
  const q = new URLSearchParams({ pageSize: '1', ...(flow ? { flow } : {}), ...(status ? { status } : {}) });
  return (await call('GET', `/_automation/runs?${q}`)).body?.total ?? 0;
}
async function waitFor(fn, timeoutMs = 60_000, every = 1000) {
  const s = Date.now();
  for (;;) { const v = await fn(); if (v) return v; if (Date.now() - s > timeoutMs) return null; await sleep(every); }
}

// ===================================================================== functional
async function functional() {
  console.log('\n== functional');
  const granted = (key) => fx.users.find((u) => u.key === key);
  const companiesOf = async (as) => (await site('GET', '/api/crm/data/companies?pageSize=200&select=name', undefined, as)).body;

  await check('public: products readable anonymously, hidden cost never served', async () => {
    const r = await site('GET', '/api/crm/data/products?pageSize=5&sort=sku');
    return r.status === 200 && r.body.items.length > 0 && r.body.items.every((x) => !('cost' in x) && !('created_by' in x)) || { ok: false, detail: r.text.slice(0, 200) };
  });
  await check('public: filtering on a hidden field is refused', async () => expect(await site('POST', '/api/crm/data/products/query', { filter: { field: 'cost', op: 'gt', value: 1 } }), 400));
  await check('public: admin-only table does not exist for the site', async () => expect(await site('GET', '/api/crm/data/audit_log'), 404));
  await check('public: rules-only table needs sign-in (anonymous)', async () => expect(await site('GET', '/api/crm/data/companies'), 401, 403));
  await check('public: create-only leads accept a POST and refuse a list', async () => {
    const c = await site('POST', '/api/crm/data/leads', { email: 'probe@example.com', name: 'Probe', company_name: 'Company 000001' });
    const l = await site('GET', '/api/crm/data/leads');
    return (c.status === 201 || c.status === 429) && l.status === 403 || { ok: false, detail: `${c.status} / ${l.status}` };
  });
  await check('public: /_model lists only public tables, without hidden fields', async () => {
    const r = await site('GET', '/api/crm/_model');
    const names = r.body.tables.map((x) => x.apiName);
    const products = r.body.tables.find((x) => x.apiName === 'products');
    return !names.includes('audit_log') && !names.includes('kpi') && names.includes('products') && !products.fields.some((x) => x.apiName === 'cost') || { ok: false, detail: names };
  });
  await check('public: OpenAPI document carries the app (and no admin-only table)', async () => {
    const r = await site('GET', '/api/openapi.json');
    const paths = Object.keys(r.body?.paths ?? {});
    measure('openapiBytes', r.text.length); measure('openapiMs', r.ms);
    return paths.includes('/api/crm/data/products') && !paths.some((p) => p.includes('audit_log')) || { ok: false, detail: paths.filter((p) => p.includes('crm')) };
  });
  await check('admin: generated TypeScript client downloads', async () => {
    const r = await fetch(`${t.admin}/api/admin/api-client.zip`, { headers: adminHeaders });
    const bytes = (await r.arrayBuffer()).byteLength; measure('apiClientZipBytes', bytes);
    return r.status === 200 && bytes > 1000 || { ok: false, detail: r.status };
  });

  // ---- row rules
  const expected = { guest01: 5, guest02: 0, sales08: Math.ceil(fx.counts.companies / 2) };
  for (const key of ['sales01', 'sales02', 'sales08', 'guest01', 'guest02']) {
    await check(`row rules: ${key} sees exactly the companies granted to them`, async () => {
      const grants = (await call('POST', '/_records/company_access/query', { filter: { field: 'user_email', op: 'eq', value: granted(key).email }, pageSize: 1 })).body.total;
      const seen = await companiesOf(key);
      const want = expected[key] ?? grants;
      return seen?.total === grants && grants === want || { ok: false, detail: `saw ${seen?.total}, grants ${grants}, expected ${want}` };
    });
  }
  await check('row rules: sales01 reaches deals only of granted companies (2 hops)', async () => {
    const mine = new Set((await companiesOf('sales01')).items.map((x) => x.id));
    const deals = await site('GET', '/api/crm/data/deals?pageSize=100&select=company', undefined, 'sales01');
    return deals.status === 200 && deals.body.total > 0 && deals.body.items.every((d) => mine.has(d.company)) || { ok: false, detail: `${deals.status} total ${deals.body?.total}` };
  });
  await check('row rules: a deal of an ungranted company is 404 for sales01', async () => {
    const mine = new Set((await companiesOf('sales01')).items.map((x) => x.id));
    const other = (await call('GET', '/_records/deals?pageSize=50&select=company')).body.items.find((d) => !mine.has(d.company));
    return expect(await site('GET', `/api/crm/data/deals/${other.id}`, undefined, 'sales01'), 404);
  });
  await check('role bypass: mgr01 (Managers, bypass) sees every company', async () => {
    const r = await site('GET', '/api/crm/data/companies?pageSize=1', undefined, 'mgr01');
    return r.body?.total === fx.counts.companies || { ok: false, detail: `${r.status} total ${r.body?.total} of ${fx.counts.companies}` };
  });
  await check('row rules: sales01 may update a granted company, not another', async () => {
    const mine = (await companiesOf('sales01')).items[0];
    const ok = await site('PATCH', `/api/crm/data/companies/${mine.id}`, { notes: 'touched by sales01' }, 'sales01');
    const notMine = (await call('GET', '/_records/companies?pageSize=1&sort=-name')).body.items[0];
    const no = await site('PATCH', `/api/crm/data/companies/${notMine.id}`, { notes: 'nope' }, 'sales01');
    return ok.status === 200 && [403, 404].includes(no.status) || { ok: false, detail: `${ok.status} ${ok.text.slice(0, 120)} / ${no.status}` };
  });
  await check('row rules WITH CHECK: an activity may only point at a reachable company', async () => {
    const mine = (await companiesOf('sales01')).items[0];
    const notMine = (await call('GET', '/_records/companies?pageSize=1&sort=-name')).body.items[0];
    const ok = await site('POST', '/api/crm/data/activities', { subject: 'probe call', kind: 'call', company: mine.id }, 'sales01');
    const no = await site('POST', '/api/crm/data/activities', { subject: 'probe call', kind: 'call', company: notMine.id }, 'sales01');
    return ok.status === 201 && no.status >= 400 && no.status < 500 || { ok: false, detail: `${ok.status} ${ok.text.slice(0, 100)} / ${no.status}` };
  });
  await check('roles: guest (no role) cannot create a deal', async () => expect(await site('POST', '/api/crm/data/deals', { title: 'x', company: fx.ids.companies[0] }, 'guest01'), 401, 403));

  // ---- own records, hidden and read-only fields, automation off a site write
  await check('own records: guest01 files an urgent ticket, guest02 cannot see it, triage flags it', async () => {
    const c = await site('POST', '/api/crm/data/tickets', { subject: 'Probe outage', priority: 'urgent' }, 'guest01');
    if (c.status !== 201) return { ok: false, detail: `create ${c.status} ${c.text.slice(0, 150)}` };
    const id = c.body.id;
    const other = await site('GET', `/api/crm/data/tickets/${id}`, undefined, 'guest02');
    const mine = await site('GET', '/api/crm/data/tickets?pageSize=5', undefined, 'guest01');
    const flagged = await waitFor(async () => (await call('GET', `/_records/tickets/${id}`)).body?.escalated === true, 60_000);
    return other.status === 404 && mine.body.items.every((x) => !('internal_notes' in x)) && flagged || { ok: false, detail: `other ${other.status}, flagged ${flagged}` };
  });
  await check('read-only field refused from the site', async () => expect(await site('POST', '/api/crm/data/tickets', { subject: 'x', escalated: true }, 'guest01'), 400));
  await check('site flow: sales01 may run recalc_company, guest01 403, anonymous 401', async () => {
    const mine = (await companiesOf('sales01')).items[0];
    const a = await site('POST', '/api/crm/flows/recalc_company/run', { input: { company: mine.id } }, 'sales01');
    const b = await site('POST', '/api/crm/flows/recalc_company/run', { input: { company: mine.id } }, 'guest01');
    const c = await site('POST', '/api/crm/flows/recalc_company/run', { input: { company: mine.id } });
    return a.status === 202 && b.status === 403 && c.status === 401 || { ok: false, detail: `${a.status} ${b.status} ${c.status}` };
  });
  await check('site flow: input over 16 KB is 413', async () => expect(await site('POST', '/api/crm/flows/recalc_company/run', { input: { pad: 'x'.repeat(17_000) } }, 'sales01'), 413));
  await check('page gating: /portal sends anonymous visitors to sign-in, lets a user in', async () => {
    // As a browser asks: without Accept text/html the edge answers 401 problem+json instead.
    const html = { accept: 'text/html' };
    const anon = await site('GET', '/portal', undefined, null, html);
    const signed = await site('GET', '/portal', undefined, 'sales01', html);
    const toSignin = anon.status === 302 && (anon.headers.get('location') ?? '').includes('signin');
    return toSignin && signed.status !== 302 || { ok: false, detail: `anon ${anon.status} ${anon.headers.get('location')}, user ${signed.status}` };
  });

  // ---- admin plane semantics
  await check('optimistic concurrency: a stale version is 409', async () => {
    const id = fx.ids.tickets[1];
    const cur = (await call('GET', `/_records/tickets/${id}`)).body;
    await call('PATCH', `/_records/tickets/${id}`, { description: `v ${Date.now()}`, version: cur.version });
    return expect(await call('PATCH', `/_records/tickets/${id}`, { status: 'closed', version: cur.version }), 409);
  });
  await check('draft: a change against a stale hash is 409', async () =>
    expect(await call('POST', '/_model/draft/changes', { expectedHash: 'stale', operations: [{ op: 'update', type: 'settings', value: { description: 'x' } }] }), 409));
  await check('revisions: list, diff 1 -> 2, rollback to 1 and forward again', async () => {
    const list = (await call('GET', '/_model/revisions')).body;
    const diff = await call('GET', '/_model/diff?from=1&to=2');
    const state = (await call('GET', '/_model')).body;
    const back = await call('POST', '/_model/rollback', { toRevision: 1, expectedPublishedHash: state.published.hash, description: 'probe rollback' });
    const fwd = await call('POST', '/_model/rollback', { toRevision: state.published.number, description: 'probe: back to what was live' });
    measure('rollbackMs', [back.ms, fwd.ms]);
    return list.total >= 2 && diff.body?.changes?.length > 0 && back.status === 200 && fwd.status === 200 || { ok: false, detail: `${list.total} ${diff.status} ${back.status} ${back.text.slice(0, 150)} ${fwd.status}` };
  });
  await check('relationships: deleting a company with deals is refused (restrict)', async () =>
    expect(await call('DELETE', `/_records/companies/${(await call('GET', `/_records/deals/${fx.ids.deals[5]}`)).body.company}`), 400, 409));
  await check('relationships: deleting a deal cascades its activities, fires deal_deleted', async () => {
    const before = await runs('deal_deleted');
    const deal = (await call('POST', '/_records/deals', { title: `Probe deal ${Date.now()}`, company: fx.ids.companies[3], amount: 10 })).body;
    const act = (await call('POST', '/_records/activities', { subject: 'child', company: fx.ids.companies[3], deal: deal.id })).body;
    const del = await call('DELETE', `/_records/deals/${deal.id}`);
    const gone = await call('GET', `/_records/activities/${act.id}`);
    const fired = await waitFor(async () => (await runs('deal_deleted')) > before, 60_000);
    return del.status === 204 && gone.status === 404 && fired || { ok: false, detail: `${del.status} ${gone.status} ${fired}` };
  });
  await check('relationships: deleting a contact clears deal.primary_contact (setNull)', async () => {
    const contact = (await call('POST', '/_records/contacts', { email: `probe${Date.now()}@example.com`, last_name: 'Probe' })).body;
    const deal = (await call('POST', '/_records/deals', { title: `Probe deal ${Date.now()}b`, company: fx.ids.companies[3], primary_contact: contact.id })).body;
    await call('DELETE', `/_records/contacts/${contact.id}`);
    return (await call('GET', `/_records/deals/${deal.id}`)).body.primary_contact == null;
  });
  await check('relationships: one-to-one refuses a second profile for a company', async () => {
    const prof = (await call('GET', '/_records/company_profiles?pageSize=1')).body.items[0];
    return expect(await call('POST', '/_records/company_profiles', { headline: 'dup', company: prof.company }), 400, 409);
  });
  await check('relationships: many-to-many, both sides', async () => {
    const d = fx.ids.deals[0];
    const p = (await call('GET', `/_records/deals/${d}/products`)).body;
    const back = p.items[0] && (await call('GET', `/_records/products/${p.items[0].id}/deals?pageSize=1`)).body;
    return p.total === 3 && back.total > 0 || { ok: false, detail: `${p.total} ${back?.total}` };
  });
  await check('unique: a duplicate company name is refused', async () => expect(await call('POST', '/_records/companies', { name: 'Company 000001' }), 400, 409));
  await check('query: filter through a lookup, multi-sort, expand, search, select', async () => {
    const r = await call('POST', '/_records/deals/query', { filter: { and: [{ field: 'company.industry', op: 'eq', value: 'software' }, { field: 'amount', op: 'gte', value: 50000 }] },
      sort: [{ field: 'amount', direction: 'desc' }, { field: 'title' }], expand: ['company'], select: ['title', 'amount', 'company'], pageSize: 20 });
    const s = await call('GET', '/_records/companies?search=Company%20000012&pageSize=5');
    return r.status === 200 && r.body.items.every((d) => d.company?.industry === 'software' && d.amount >= 50000) && s.body.total >= 1 || { ok: false, detail: r.text.slice(0, 200) };
  });

  // ---- automation chains
  await check('forms -> flow: a site form submission becomes a lead, then lead_intake runs', async () => {
    const before = await runs('lead_intake', 'succeeded');
    const r = await site('POST', '/api/contact/forms/lead', { email: 'form-probe@example.com', name: 'Form probe', message: 'hello' });
    if (r.status >= 300) return { ok: false, detail: `${r.status} ${r.text.slice(0, 150)}` };
    return !!await waitFor(async () => (await runs('form_to_lead', 'succeeded')) > 0 && (await runs('lead_intake', 'succeeded')) > before, 90_000);
  });
  await check('flow.invoke: chain runs recalc_company one level deeper', async () => {
    const r = await call('POST', '/_automation/flows/chain/run', { input: { company: fx.ids.companies[0] } });
    const run = await waitFor(async () => { const d = (await call('GET', `/_automation/runs/${r.body.runId}`)).body; return d?.chain?.length >= 2 && d.chain.every((x) => x.status === 'succeeded') ? d : null; }, 60_000);
    return run && run.chain.map((x) => `${x.flow}@${x.depth}`).join(',') === 'chain@0,recalc_company@1' || { ok: false, detail: run?.chain };
  });
  await check('cascade limit: a self-retriggering flow is terminated', async () => {
    const row = (await call('POST', '/_records/loop_counter', { name: `probe ${Date.now()}` })).body;
    await call('PATCH', `/_records/loop_counter/${row.id}`, { name: 'go' });
    const done = await waitFor(async () => (await call('GET', '/_automation/runs?flow=loop_bump&status=terminated&pageSize=1')).body.items[0], 90_000);
    return done?.error?.includes('cascad') || { ok: false, detail: done };
  });
  await check('revision.published and relation.created flows ran', async () => (await runs('on_publish', 'succeeded')) > 0 && (await runs('product_linked', 'succeeded')) > 0);
  await check('user.invited / user.activated flows ran for the site users', async () => {
    const inv = await runs('user_invited', 'succeeded'), act = await runs('user_activated', 'succeeded');
    return inv >= 12 && act >= 12 || { ok: false, detail: `invited ${inv}, activated ${act}` };
  });
  await check('email action: digest flow sends one email', async () => {
    const r = await call('POST', '/_automation/flows/digest_email/run', { input: { to: profile.auth.username } });
    const d = await waitFor(async () => { const x = (await call('GET', `/_automation/runs/${r.body.runId}`)).body; return ['succeeded', 'failed'].includes(x?.run?.status) ? x : null; }, 60_000);
    return d?.run.status === 'succeeded' || { ok: false, detail: d?.run };
  });
  await check('failed run can be retried by hand', async () => {
    const r = await call('POST', '/_automation/flows/recalc_company/run', { input: { company: '00000000-0000-0000-0000-000000000001' } });
    const f = await waitFor(async () => (await call('GET', `/_automation/runs/${r.body.runId}`)).body?.run?.status === 'failed', 60_000);
    return f && (await call('POST', `/_automation/runs/${r.body.runId}/retry`)).status === 202;
  });
}

// ===================================================================== limits
async function limits() {
  console.log('\n== limits');
  const q = (body) => call('POST', '/_records/deals/query', body);
  await check('pageSize 200 ok, 201 refused or clamped', async () => {
    const ok = await call('GET', '/_records/deals?pageSize=200'), big = await call('GET', '/_records/deals?pageSize=201');
    measure('pageSize201', { status: big.status, returned: big.body?.items?.length });
    return ok.body.items.length === 200 && (big.status === 400 || big.body?.items?.length <= 200);
  });
  await check('offset beyond 100,000 refused', async () => expect(await call('GET', '/_records/deals?pageSize=200&page=502'), 400));
  await check('21 predicates refused', async () => expect(await q({ filter: { or: Array.from({ length: 21 }, (_, i) => ({ field: 'amount', op: 'eq', value: i })) } }), 400));
  await check('nesting deeper than 5 refused', async () => {
    let f = { field: 'amount', op: 'gt', value: 1 }; for (let i = 0; i < 6; i++) f = { not: f };
    return expect(await q({ filter: f }), 400);
  });
  await check('in-list of 101 refused', async () => expect(await q({ filter: { field: 'amount', op: 'in', value: Array.from({ length: 101 }, (_, i) => i) } }), 400));
  await check('4 sort keys refused', async () => expect(await q({ sort: ['amount', 'title', 'stage', 'close_date'].map((field) => ({ field })) }), 400));
  await check('change set of 201 operations refused', async () => {
    const st = (await call('GET', '/_model')).body;
    return expect(await call('POST', '/_model/draft/changes', { expectedHash: st.hash, operations: Array.from({ length: 201 }, () => ({ op: 'update', type: 'settings', value: { description: 'x' } })) }), 400);
  });
  // Past the request ceilings the work becomes a tracked job (dcms.jobs@2), no longer a 400.
  const asJob = (r) => r.status === 202 && r.body?.jobId ? { ok: true } : { ok: false, detail: `${r.status} ${r.text.slice(0, 200)}` };
  await check('import of 1,001 rows runs as a job', async () => asJob(await call('POST', '/_records/audit_log/import?dryRun=true', { rows: Array.from({ length: 1001 }, (_, i) => ({ message: `m${i}` })) })));
  await check('import is all or nothing: one bad row writes nothing', async () => {
    const before = (await call('GET', '/_records/audit_log?pageSize=1&select=id')).body.total;
    const r = await call('POST', '/_records/audit_log/import?dryRun=false', { rows: [...Array.from({ length: 50 }, (_, i) => ({ message: `aon ${i}` })), { level: 'no message' }] });
    const after = (await call('GET', '/_records/audit_log?pageSize=1&select=id')).body.total;
    return r.body?.created === 0 && r.body?.errorCount === 1 && after - before < 50 || { ok: false, detail: `${r.status} ${r.text.slice(0, 150)} ${before}->${after}` };
  });
  await check('bulk update of 501 ids runs as a job', async () => asJob(await call('POST', '/_records/deals/bulk-update', { ids: Array.from({ length: 501 }, () => fx.ids.deals[0]), values: { priority: 'low' } })));
  await check('record over 256 KB refused', async () => expect(await call('POST', '/_records/companies', { name: `Huge ${Date.now()}`, notes: 'x'.repeat(260_000) }), 400, 413));
  await check('public write throttle: 429 within 120 writes an hour from one address', async () => {
    let n = 0, last;
    for (; n < 130; n++) { last = await site('POST', '/api/crm/data/leads', { email: `throttle${n}@example.com` }); if (last.status === 429) break; }
    measure('publicWritesBefore429', n);
    return last.status === 429 || { ok: false, detail: `${n} writes, last ${last.status}` };
  });
  await check('signed-in site user shares the address allowance (no fresh budget)', async () =>
    expect(await site('POST', '/api/crm/data/tickets', { subject: 'after throttle' }, 'guest02'), 429));
}

// ===================================================================== automation storm
const STATUSES = ['succeeded', 'skipped', 'failed', 'terminated', 'pending', 'running'];
async function statuses(flows) {
  const out = {};
  for (const f of flows) { out[f] = {}; for (const st of STATUSES) out[f][st] = await runs(f, st); out[`${f}_total`] = await runs(f); }
  return out;
}
const delta = (a, b) => Object.fromEntries(Object.entries(b).filter(([f]) => !f.endsWith('_total')).map(([f, x]) => [f, Object.fromEntries(STATUSES.map((st) => [st, x[st] - a[f][st]]).filter(([, v]) => v))]));
const FLOWS = ['deal_won', 'on_deal_closed', 'activity_touch'];

/**
 * Waits until `flow` has `expected` more runs than `base` (any status: the outbox must have routed
 * every event), then until nothing is pending or running. Records when the first and the last
 * expected run appeared (routing lag) and when the queue drained.
 */
async function drain(started, label, flow, base, expected) {
  const samples = [];
  let firstAt = null, routedAt = null;
  const ok = await waitFor(async () => {
    const got = (await runs(flow)) - base;
    const pending = await runs(null, 'pending') + await runs(null, 'running');
    const sec = Math.round((Date.now() - started) / 1000);
    samples.push({ s: sec, [flow]: got, pending });
    if (got > 0 && firstAt === null) firstAt = Date.now() - started;
    if (got >= expected && routedAt === null) routedAt = Date.now() - started;
    if (samples.length % 10 === 0) console.log(`  ${label} t+${sec}s ${flow} ${got}/${expected}, pending/running ${pending}`);
    return got >= expected && pending === 0;
  }, 60 * 60_000, 2000);
  return { completed: !!ok, firstRunMs: firstAt, allRoutedMs: routedAt, drainedMs: Date.now() - started, samples: samples.filter((_, i) => i % 5 === 0) };
}

async function wonCandidates(n) {
  const out = [];
  for (let page = 1; out.length < n; page++) {
    const r = (await call('POST', '/_records/deals/query', { filter: { field: 'stage', op: 'nin', value: ['won'] }, select: ['id'], pageSize: 200, page })).body;
    out.push(...r.items.map((x) => x.id)); if (r.items.length < 200) break;
  }
  return out.slice(0, n);
}

async function automation() {
  console.log('\n== automation');

  // 1. Import: does every imported row run its row-created flow?
  await check('import: every imported urgent ticket is escalated by ticket_triage', async () => {
    const tag = `imp${Date.now()}`;
    const base = await runs('ticket_triage');
    const r = await call('POST', '/_records/tickets/import?dryRun=false', { rows: Array.from({ length: 100 }, (_, i) => ({ subject: `${tag} ${i}`, priority: 'urgent' })) });
    if (r.status !== 200) return { ok: false, detail: r.text.slice(0, 200) };
    measure('importDrain', await drain(Date.now() - r.ms, 'import', 'ticket_triage', base, 100));
    const escalated = (await call('POST', '/_records/tickets/query', { filter: { and: [{ field: 'subject', op: 'startsWith', value: tag }, { field: 'escalated', op: 'eq', value: true }] }, pageSize: 1 })).body.total;
    measure('importFlowCoverage', { imported: 100, escalated });
    return escalated === 100 || { ok: false, detail: `${escalated}/100 escalated; the rest hit "one action has already set off 20 flow runs"` };
  });

  // 2. Bulk storm: one bulk-update call is one action.
  const bulkN = Math.min(500, Math.round(fx.counts.deals / 20));
  let before = await statuses(FLOWS);
  let s = Date.now();
  const bulkIds = await wonCandidates(bulkN);
  const b = await call('POST', '/_records/deals/bulk-update', { ids: bulkIds, values: { stage: 'won' } });
  const bulkDrain = await drain(s, 'bulk', 'deal_won', before.deal_won_total, bulkIds.length);
  const bulk = delta(before, await statuses(FLOWS));
  measure('bulkStorm', { deals: bulkIds.length, bulkUpdateMs: b.ms, ...bulkDrain, runs: bulk });
  await check('bulk update: every deal moved to won runs deal_won', async () =>
    (bulk.deal_won?.succeeded ?? 0) === bulkIds.length || { ok: false, detail: bulk });

  // 3. Per-record storm: one PATCH per deal (each its own action), 8 in flight. Worker throughput.
  const n = Math.min(1000, Math.round(fx.counts.deals / 30));
  const ids = await wonCandidates(n);
  before = await statuses(FLOWS);
  s = Date.now();
  const patches = await pool(ids, 8, (id) => call('PATCH', `/_records/deals/${id}`, { stage: 'won' }));
  const writeMs = Date.now() - s;
  const stormDrain = await drain(s, 'storm', 'deal_won', before.deal_won_total, ids.length);
  // on_deal_closed follows deal_won; let it finish too.
  await drain(s, 'storm-tail', 'on_deal_closed', before.on_deal_closed_total, 0);
  const totalMs = Date.now() - s;
  const storm = delta(before, await statuses(FLOWS));
  const ran = Object.values(storm).reduce((a, x) => a + (x.succeeded ?? 0) + (x.skipped ?? 0) + (x.failed ?? 0), 0);
  measure('perRecordStorm', { deals: ids.length, patchErrors: patches.filter((r) => r.status !== 200).length, writeMs, drainMs: totalMs,
    runsExecuted: ran, runsPerSec: +(ran / (totalMs / 1000)).toFixed(1), runs: storm, ...stormDrain });
  await check('per-record storm: every deal_won ran, none failed', async () =>
    (storm.deal_won?.succeeded ?? 0) === ids.length || { ok: false, detail: storm });
  // 4. Contention: every deal of 10 companies won at once, so concurrent deal_won runs update the
  // same company. Each must succeed, and each company's total must be the exact sum.
  const base = 2000 + Math.floor(Date.now() / 1000) % 2900;
  const names = Array.from({ length: 10 }, (_, k) => `Company ${String(base + k * 7).padStart(6, '0')}`);
  const targets = (await call('POST', '/_records/companies/query', { filter: { field: 'name', op: 'in', value: names }, select: ['name', 'total_won'], pageSize: 10 })).body.items;
  const contended = [];
  for (const c of targets) {
    const r = (await call('POST', '/_records/deals/query', { filter: { and: [{ field: 'company', op: 'eq', value: c.id }, { field: 'stage', op: 'ne', value: 'won' }] }, select: ['amount'], pageSize: 20 })).body;
    contended.push(...r.items.map((d) => ({ ...d, company: c.id, before: Number(c.total_won ?? 0) })));
  }
  const failedBefore = await runs('deal_won', 'failed');
  const cBase = await runs('deal_won');
  const cs = Date.now();
  await pool(contended, 8, (d) => call('PATCH', `/_records/deals/${d.id}`, { stage: 'won' }));
  await drain(cs, 'contention', 'deal_won', cBase, contended.length);
  const failedRuns = (await runs('deal_won', 'failed')) - failedBefore;
  let wrong = 0;
  for (const c of targets) {
    const mine = contended.filter((d) => d.company === c.id);
    const want = mine[0]?.before + mine.reduce((a, d) => a + Number(d.amount), 0);
    const got = Number((await call('GET', `/_records/companies/${c.id}`)).body.total_won ?? 0);
    if (mine.length && Math.abs(want - got) > 0.01) wrong++;
  }
  measure('contention', { companies: targets.length, deals: contended.length, failedRuns, companiesWithWrongTotal: wrong, ms: Date.now() - cs });
  await check('contention: concurrent runs on one company all succeed and add up exactly', async () =>
    failedRuns === 0 && wrong === 0 || { ok: false, detail: `${failedRuns} failed runs, ${wrong} wrong totals` });

  await check('storm side effects: won_at stamped, companies totalled, audit rows written', async () => {
    const d = (await call('GET', `/_records/deals/${ids[0]}?expand=company`)).body;
    const audit = (await call('POST', '/_records/audit_log/query', { filter: { field: 'ref', op: 'eq', value: ids[0] }, pageSize: 5 })).body.total;
    return d.won_at && d.company.total_won > 0 && audit >= 1 || { ok: false, detail: { won_at: d.won_at, total: d.company?.total_won, audit } };
  });
}

// ===================================================================== scale
async function scale() {
  console.log('\n== scale (latency at full data size, 10 sequential calls each)');
  const L = {};
  const last = Math.floor(Math.min(fx.counts.activities, 100_000) / 200);
  L.adminActivitiesPage1 = await timed(10, () => call('GET', '/_records/activities?pageSize=50&sort=-due_at'));
  L.adminActivitiesDeepPage = await timed(10, () => call('GET', `/_records/activities?pageSize=200&page=${last}&sort=-due_at`));
  L.adminDealsFilterLookup = await timed(10, () => call('POST', '/_records/deals/query', { filter: { field: 'company.industry', op: 'eq', value: 'energy' }, sort: [{ field: 'amount', direction: 'desc' }], pageSize: 50 }));
  L.adminDealsExpand2 = await timed(10, () => call('GET', '/_records/deals?pageSize=50&expand=company,primary_contact'));
  L.adminContactsSearch = await timed(10, () => call('GET', '/_records/contacts?search=Surname0123&pageSize=20'));
  L.adminCompanyRelated = await timed(10, () => call('GET', `/_records/companies/${fx.ids.companies[0]}/activities?pageSize=50`));
  L.adminGetOne = await timed(10, () => call('GET', `/_records/deals/${fx.ids.deals[7]}?expand=company`));
  L.siteProductsAnon = await timed(10, () => site('GET', '/api/crm/data/products?pageSize=50&sort=name'));
  for (const who of ['sales01', 'sales08', 'mgr01']) {
    L[`siteDeals_${who}`] = await timed(10, () => site('GET', '/api/crm/data/deals?pageSize=50&sort=-amount', undefined, who));
    L[`siteActivities_${who}`] = await timed(10, () => site('GET', '/api/crm/data/activities?pageSize=50', undefined, who));
  }
  L.modelGet = await timed(10, () => call('GET', '/_model/published'));
  L.openapi = await timed(5, () => site('GET', '/api/openapi.json'));
  measure('latency', L);

  // Publishing over data: a new unique constraint must be checked against every live row.
  const st = (await call('GET', '/_model')).body;
  const applied = await call('POST', '/_model/draft/changes', { expectedHash: st.hash, operations: [{ op: 'update', type: 'field', target: 'contacts.last_name', value: { unique: true } }] });
  const v = await call('POST', '/_model/draft/validate');
  const pv = await call('GET', '/_model/draft/preview');
  const pub = await call('POST', '/_model/draft/publish', { expectedHash: applied.body?.draft?.hash });
  const st2 = (await call('GET', '/_model')).body;
  const undo = await call('POST', '/_model/draft/changes', { expectedHash: st2.hash, operations: [{ op: 'update', type: 'field', target: 'contacts.last_name', value: { unique: false } }] });
  const pub2 = await call('POST', '/_model/draft/publish', { expectedHash: undo.body?.draft?.hash });
  measure('uniqueOverData', { rows: fx.counts.contacts, applyMs: applied.ms, validateMs: v.ms, previewMs: pv.ms, publishMs: pub.ms, publishStatus: pub.status, revertPublishMs: pub2.ms });
  await check('a new unique constraint over existing rows publishes (and reverts)', async () => pub.status === 200 && pub2.status === 200 || { ok: false, detail: `${pub.status} ${pub.text.slice(0, 200)} / ${pub2.status}` });

  // Import throughput on the heaviest path: 5 calls of 1,000 activities, each naming its company
  // and its deal by name (two lookups resolved per row), into a 50k-row table.
  const titles = (await call('GET', '/_records/deals?pageSize=200&select=title')).body.items.map((d) => d.title);
  const companies = (await call('GET', '/_records/companies?pageSize=200&select=name')).body.items.map((c) => c.name);
  const tag = `imp-${Date.now()}`;
  const importCalls = [];
  for (let k = 0; k < 5; k++) {
    const rows = Array.from({ length: 1000 }, (_, i) => ({ subject: `${tag} ${k}-${i}`, kind: i % 4 === 0 ? 'meeting' : 'call',
      company: companies[(k * 1000 + i) % companies.length], deal: titles[(k * 1000 + i) % titles.length] }));
    const r = await call('POST', '/_records/activities/import?dryRun=false', { rows });
    importCalls.push({ status: r.status, ms: r.ms, rowsPerSec: Math.round(1000 / (r.ms / 1000)), error: r.status === 200 ? undefined : r.text.slice(0, 160) });
  }
  measure('importActivities5x1000', importCalls);

  // Parallel imports: 4 calls of 1,000 rows at once into one table.
  const par = await Promise.all([0, 1, 2, 3].map((k) => call('POST', '/_records/audit_log/import?dryRun=false', { rows: Array.from({ length: 1000 }, (_, i) => ({ message: `parallel ${k}-${i}`, level: 'debug' })) })));
  measure('parallelImport4x1000', par.map((r) => ({ status: r.status, ms: r.ms })));

  // Concurrent single creates from the admin, 16 in flight.
  const s = Date.now();
  const res = await pool(Array.from({ length: 400 }, (_, i) => i), 16, (i) => call('POST', '/_records/audit_log', { message: `concurrent ${i}` }));
  measure('adminCreates400at16', { ms: Date.now() - s, perSec: Math.round(400 / ((Date.now() - s) / 1000)), errors: res.filter((r) => r.status !== 201).length });
}

// ===================================================================== model limits
// A second instance, `limits`, at the validator's maximums: 100 tables of 200 fields, all public.
async function model() {
  console.log('\n== model limits (instance /limits)');
  const L = '/api/admin/plugins/limits';
  const inst = (await call('GET', '/api/admin/plugins/instances')).body.find((x) => x.slug === 'limits')
    ?? (await call('POST', '/api/admin/plugins/instances', { pluginId: 'dynamic-apps', slug: 'limits', name: 'Model limits', config: '{}' })).body;
  const types = ['text', 'integer', 'decimal', 'boolean', 'date', 'dateTime', 'email', 'url', 'longText', 'json'];
  const table = (t, fields = 200) => ({ op: 'create', type: 'table', value: { apiName: `t${String(t).padStart(3, '0')}`, displayName: `Table ${t}`,
    primaryFieldId: 'f000', public: { read: 'all' },
    fields: Array.from({ length: fields }, (_, k) => ({ apiName: `f${String(k).padStart(3, '0')}`, displayName: `Field ${k}`, type: k === 0 ? 'text' : types[k % types.length],
      sortable: k < 5, filterable: k < 5, searchable: k === 0 })) } });
  let st = (await call('GET', `${L}/_model`)).body;
  if (st.draft) { await call('POST', `${L}/_model/draft/discard`, { expectedHash: st.draft.hash }); st = (await call('GET', `${L}/_model`)).body; }
  let tables = st.published ? (await call('GET', `${L}/_model/published`)).body.config.tables.length : 0;
  if (tables < 100) {
    // The validator allows 100 tables of 200 fields; add them 5 at a time (from wherever an
    // earlier run stopped) until something refuses, then publish what fits.
    const applyMs = [];
    let hash = st.hash, refusal = null;
    while (tables < 100) {
      const r = await call('POST', `${L}/_model/draft/changes`, { expectedHash: hash, operations: Array.from({ length: 5 }, (_, k) => table(tables + k)) });
      if (r.status !== 200) { refusal = r.body?.error ?? r.text.slice(0, 200); break; }
      applyMs.push(r.ms); hash = r.body.draft.hash; tables += 5;
    }
    const draft = await call('GET', `${L}/_model/draft`);
    const v = await call('POST', `${L}/_model/draft/validate`);
    const pub = await call('POST', `${L}/_model/draft/publish`, { expectedHash: hash });
    measure('modelCeiling', { tablesOf200Fields: tables, fields: tables * 200, refusal, draftDocChars: draft.text.length,
      applyMsFirstLast: [applyMs[0], applyMs.at(-1)], validateMs: v.ms,
      validationErrors: v.body?.issues?.filter((x) => x.severity === 'error').length, publishMs: pub.ms, publishStatus: pub.status });
    st = (await call('GET', `${L}/_model`)).body;
  }
  await check('validator maximum (100 tables x 200 fields) fits in a configuration', async () => tables === 100 || { ok: false, detail: `${tables} tables fit` });
  const live = await call('GET', `${L}/_model/published`);
  const openapi = await site('GET', '/api/openapi.json');
  const pubModel = await site('GET', '/api/limits/_model');
  measure('model100x200Read', { publishedDocBytes: live.text.length, publishedMs: live.ms, openapiBytes: openapi.text.length, openapiMs: openapi.ms,
    openapiStatus: openapi.status, siteModelBytes: pubModel.text.length, siteModelMs: pubModel.ms });
  measure('openapiWarm', await timed(5, () => site('GET', '/api/openapi.json')));
  const t101 = await call('POST', `${L}/_model/draft/changes`, { expectedHash: st.hash, operations: [table(tables, 3)] });
  const v101 = await call('POST', `${L}/_model/draft/validate`);
  await check(`one more table beyond ${tables} is refused (apply or validation)`, async () => t101.status !== 200 || v101.body?.issues?.some((x) => x.severity === 'error') || { ok: false, detail: `${t101.status} ${v101.text.slice(0, 200)}` });
  await call('POST', `${L}/_model/draft/discard`, { expectedHash: v101.body?.revision?.hash ?? t101.body?.draft?.hash });
  st = (await call('GET', `${L}/_model`)).body;
  const f201 = await call('POST', `${L}/_model/draft/changes`, { expectedHash: st.hash, operations: [{ op: 'create', type: 'field', target: 't000', value: { apiName: 'f200', displayName: 'Field 200' } }] });
  const v201 = await call('POST', `${L}/_model/draft/validate`);
  await check('the 201st field is refused (apply or validation)', async () => f201.status !== 200 || v201.body?.issues?.some((x) => x.severity === 'error') || { ok: false, detail: `${f201.status} ${v201.text.slice(0, 200)}` });
  await call('POST', `${L}/_model/draft/discard`, { expectedHash: v201.body?.revision?.hash ?? f201.body?.draft?.hash });

  // Wide records: 1,000 rows of all 200 fields, then the shapes a list needs.
  const have = (await call('GET', `${L}/_records/t000?pageSize=1&select=id`)).body?.total ?? 0;
  if (have < 1000) {
    const val = (k, i) => [`v${i}`, i * k, i + k / 10, i % 2 === 0, '2026-01-01', '2026-01-01T10:00:00Z', `u${i}@example.com`, `https://e.example/${i}`, 'long '.repeat(20), { i, k }][k % 10];
    const rows = Array.from({ length: 1000 }, (_, i) => Object.fromEntries(Array.from({ length: 200 }, (_, k) => [`f${String(k).padStart(3, '0')}`, k === 0 ? `row ${i}` : val(k, i)])));
    const r = await call('POST', `${L}/_records/t000/import?dryRun=false`, { rows });
    measure('wideImport1000x200', { status: r.status, ms: r.ms, bodyBytes: JSON.stringify(rows).length, error: r.status === 200 ? undefined : r.text.slice(0, 200) });
  }
  measure('wideList', await timed(5, () => call('GET', `${L}/_records/t000?pageSize=200&sort=f001`)));
  measure('wideListSite', await timed(5, () => site('GET', '/api/limits/data/t000?pageSize=200')));
}

// ===================================================================== scripts
// script.run@1 (ADR 0025): every step is a fresh Deno process in script-runner, two at a time
// (ScriptRunner__MaxConcurrency), behind a 15 s client timeout. The table and flow are added to
// `crm` on first use. 10 % of rows run a CPU-heavy script, to see whether a few slow scripts
// holding both slots starve the rest.
const SCRIPT_MODEL = [
  { op: 'create', type: 'table', value: { apiName: 'script_jobs', displayName: 'Script job', primaryFieldId: 'label',
    fields: [f('label', 'text', { required: true }), f('n', 'integer'), f('score', 'integer')] } },
  { op: 'create', type: 'flow', value: { apiName: 'script_score', displayName: 'Score (script)',
    trigger: { event: 'row.created', tableId: 'script_jobs' },
    steps: [
      { id: 'score', action: 'script.run@1', input: { language: 'ts', code: [
        'export default function main(ctx: { row: { n: number } }) {',
        '  let x = 0;',
        '  for (let i = 0; i < ctx.row.n; i++) x = (x * 31 + i) % 1000003;',
        '  return { score: x };',
        '}'].join('\n') } },
      { id: 'save', action: 'records.update@1', input: { table: 'script_jobs', id: '{{ row.id }}', values: { score: '{{ steps.score.score }}' } } },
    ] } },
];
function f(apiName, type, extra = {}) { return { apiName, displayName: apiName, type, ...extra }; }

async function scripts() {
  console.log('\n== scripts (script.run@1)');
  const live = (await call('GET', '/_model/published')).body.config;
  if (!live.flows.some((x) => x.apiName === 'script_score')) {
    const st = (await call('GET', '/_model')).body;
    const applied = await call('POST', '/_model/draft/changes', { expectedHash: st.hash, operations: SCRIPT_MODEL, description: 'DA stress: script.run storm' });
    if (applied.status !== 200) return check('scripts: model applied', async () => ({ ok: false, detail: applied.text.slice(0, 300) }));
    const pub = await call('POST', '/_model/draft/publish', { expectedHash: applied.body.draft.hash });
    console.log(`  script_score published (${pub.status})`);
    await sleep(5000);
  }
  const scored = async (tag) => (await call('POST', '/_records/script_jobs/query',
    { filter: { and: [{ field: 'label', op: 'startsWith', value: tag }, { field: 'score', op: 'isNull', value: false }] }, pageSize: 1 })).body?.total ?? 0;

  // 1. Idle latency: one row at a time, create -> score written.
  const one = [];
  for (let i = 0; i < 10; i++) {
    const tag = `one${Date.now()}`;
    const s = Date.now();
    await call('POST', '/_records/script_jobs', { label: tag, n: 1000 });
    if (await waitFor(() => scored(tag), 60_000, 200)) one.push(Date.now() - s);
  }
  one.sort((a, b) => a - b);
  measure('scriptIdleLatency', { n: one.length, p50: one[5], max: one.at(-1) });

  // 2. Storm: one import of 1,000 rows (each row its own action), 10 % heavy.
  for (const [label, heavyN] of [['light', 1000], ['heavy10pct', 300_000_000]]) {
    const tag = `st${label}${Date.now()}`;
    const rows = Array.from({ length: 1000 }, (_, i) => ({ label: `${tag} ${i}`, n: i % 10 === 0 ? heavyN : 1000 }));
    const before = { ok: await runs('script_score', 'succeeded'), failed: await runs('script_score', 'failed'), terminated: await runs('script_score', 'terminated') };
    const base = await runs('script_score');
    const s = Date.now();
    const r = await call('POST', '/_records/script_jobs/import?dryRun=false', { rows });
    if (r.status !== 200) { await check(`scripts ${label}: import`, async () => ({ ok: false, detail: r.text.slice(0, 200) })); continue; }
    const d = await drain(s, `scripts-${label}`, 'script_score', base, rows.length);
    const after = { ok: await runs('script_score', 'succeeded'), failed: await runs('script_score', 'failed'), terminated: await runs('script_score', 'terminated') };
    const delta = Object.fromEntries(Object.keys(after).map((k) => [k, after[k] - before[k]]));
    measure(`scriptStorm_${label}`, { rows: rows.length, importMs: r.ms, runsPerSec: +(rows.length / (d.drainedMs / 1000)).toFixed(1),
      scored: await scored(tag), ...delta, ...d });
    await check(`scripts ${label}: all 1,000 runs succeed and write their score`, async () =>
      delta.ok === rows.length && (await scored(tag)) === rows.length || { ok: false, detail: delta });
  }
}

const sections = { functional, limits, automation, scale, model, scripts };
for (const [name, fn] of Object.entries(sections)) if (!only || only === name) await fn();

report.finishedAt = new Date().toISOString();
report.failed = failed;
const dir = join(LOADTEST_ROOT, 'runs');
await mkdir(dir, { recursive: true });
const path = join(dir, `da-probe-${fx.tenant}-${only ?? 'all'}-${report.startedAt.replace(/[:.]/g, '')}.json`);
await writeFile(path, JSON.stringify(report, null, 2));
console.log(`\n${report.checks.length - failed}/${report.checks.length} checks passed; report ${path}`);
process.exit(failed ? 1 : 0);
