#!/usr/bin/env node
// Provisions the Dynamic Apps stress tenant and writes loadtest/fixtures/da-<env>.json.
//
//   node loadtest/seed/dynamic-apps.mjs --env vps1                      # tenant da-stress, scale 1
//   node loadtest/seed/dynamic-apps.mjs --env vps1 -e da.scale=0.1      # a quick, small one
//
// The tenant is deliberately NOT torn down by teardown.mjs (its slug lacks the loadtest prefix):
// it is kept for inspection. Idempotent: an existing tenant, instance, model, user or table that
// already holds its rows is adopted, so an interrupted run can simply be repeated.
//
// What it builds (scale 1): a Mode C site with a provisioned domain; User Authentication with
// 12 invited-and-activated site users in 2 groups, 2 roles (one bypassing row rules) and a gated
// /portal path; Forms with a `lead` form; Dynamic Apps instance `crm` with the model in
// da-model.mjs, published twice; ~115k records imported through the bulk import (1,000 rows a
// call, timed per call), m2m links, and row-access grants per user.

import { loadProfile, parseArgs, targets } from '../lib/config.mjs';
import { edgeSession, realmSiteSession } from '../lib/auth.mjs';
import { AdminApi, HttpError } from '../lib/api.mjs';
import { writeFixtures, fixturesPath } from '../lib/fixtures.mjs';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { ensureSite } from './provision.mjs';
import { MODEL, MODEL_V2 } from './da-model.mjs';
import { randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const log = (...args) => console.log('[da-seed]', ...args);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Deterministic data: the same scale gives the same rows on every run. */
function rng(seed) {
  return () => {
    seed |= 0; seed = (seed + 0x6D2B79F5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const rand = rng(20261011);
const pick = (xs) => xs[Math.floor(rand() * xs.length)];
const int = (lo, hi) => lo + Math.floor(rand() * (hi - lo + 1));
const pad = (n, w = 6) => String(n).padStart(w, '0');
const day = (offset) => new Date(Date.UTC(2026, 0, 1) + offset * 86_400_000).toISOString().slice(0, 10);

/** Runs fn over items with at most n in flight. */
export async function pool(items, n, fn) {
  const out = new Array(items.length);
  let next = 0;
  await Promise.all(Array.from({ length: Math.min(n, items.length) }, async () => {
    while (next < items.length) { const i = next++; out[i] = await fn(items[i], i); }
  }));
  return out;
}

async function ensureInstance(api, pluginId, slug, name, config = {}) {
  const found = (await api.get('/api/admin/plugins/instances')).find((x) => x.slug === slug);
  if (found) return found;
  const created = await api.post('/api/admin/plugins/instances', { pluginId, slug, name, config: JSON.stringify(config) });
  await api.post(`/api/admin/plugins/instances/${created.id}/enable`, undefined, { expect: [200, 204, 409] });
  log(`installed ${pluginId} as /${slug}`);
  return created;
}

// ---------------------------------------------------------------- the app model

async function ensureModel(app) {
  let state = await app.get('/_model');
  const timings = {};
  if (!state.published) {
    let t = Date.now();
    const applied = await app.post('/_model/draft/changes', { expectedHash: state.hash, operations: MODEL, description: 'DA stress model v1' });
    timings.applyV1Ms = Date.now() - t;
    const errors = applied.issues.filter((i) => i.severity === 'error');
    for (const i of applied.issues) log(`  ${i.severity} ${i.code} ${i.path}: ${i.message}`);
    if (errors.length) throw new Error(`The model has ${errors.length} validation error(s); see above.`);
    t = Date.now();
    const published = await app.post('/_model/draft/publish', { expectedHash: applied.draft.hash });
    timings.publishV1Ms = Date.now() - t;
    log(`model v1 published as revision ${published.revision.number} (apply ${timings.applyV1Ms} ms, publish ${timings.publishV1Ms} ms)`);
    state = await app.get('/_model');
  }
  return { state, timings };
}

/** Sorted-key JSON, so a flow compares equal whatever order the server writes its keys in. */
const canonical = (v) => JSON.stringify(v, (_, x) => (x && typeof x === 'object' && !Array.isArray(x)
  ? Object.fromEntries(Object.entries(x).sort(([a], [b]) => a.localeCompare(b))) : x));

/** Brings every fixture flow's steps and condition up to da-model.mjs, in one published change set. */
async function ensureFlowsCurrent(app) {
  const live = new Map((await app.get('/_model/published')).config.flows.map((f) => [f.apiName, f]));
  const ops = MODEL.filter((o) => o.type === 'flow')
    .filter((o) => canonical(o.value.steps) !== canonical(live.get(o.value.apiName)?.steps ?? null)
      || (o.value.condition ?? null) !== (live.get(o.value.apiName)?.condition ?? null))
    .map((o) => ({ op: 'update', type: 'flow', target: o.value.apiName, value: { steps: o.value.steps, condition: o.value.condition ?? null } }));
  if (!ops.length) return;
  const state = await app.get('/_model');
  const applied = await app.post('/_model/draft/changes', { expectedHash: state.hash, operations: ops, description: 'DA stress: flows brought up to date' });
  const published = await app.post('/_model/draft/publish', { expectedHash: applied.draft.hash });
  log(`updated ${ops.length} flow(s) (${ops.map((o) => o.target).join(', ')}) in revision ${published.revision.number}`);
}

/** v2 goes on AFTER the data, so its publish reconciles against every imported row. */
async function ensureModelV2(app) {
  const live = await app.get('/_model/published');
  if (live.config.tables.find((t) => t.apiName === 'deals').fields.some((x) => x.apiName === 'source')) return {};
  const state = await app.get('/_model');
  let t = Date.now();
  const applied = await app.post('/_model/draft/changes', { expectedHash: state.hash, operations: MODEL_V2, description: 'DA stress model v2' });
  const applyMs = Date.now() - t;
  t = Date.now();
  const published = await app.post('/_model/draft/publish', { expectedHash: applied.draft.hash });
  const publishMs = Date.now() - t;
  log(`model v2 published as revision ${published.revision.number} over the data (apply ${applyMs} ms, publish ${publishMs} ms)`);
  return { applyV2Ms: applyMs, publishV2OverDataMs: publishMs };
}

// ---------------------------------------------------------------- users and access

const USERS = [
  ...Array.from({ length: 8 }, (_, i) => ({ key: `sales${pad(i + 1, 2)}`, group: 'Sales' })),
  { key: 'mgr01', group: 'Managers' }, { key: 'mgr02', group: 'Managers' },
  { key: 'guest01', group: null }, { key: 'guest02', group: null },
];
const emailOf = (key) => `test+da-${key}@highgeek.eu`;

async function ensureRealm(users) {
  for (let i = 0; i < 30; i++) {
    const realm = await users.get('/realm');
    if (realm.clientReady && realm.hosts?.length) return realm;
    await sleep(2000);
  }
  throw new Error('The realm never became ready (no hosts or no sign-in client) -- does the site have its domain?');
}

async function ensureGroups(users) {
  const have = new Map((await users.get('/groups')).map((g) => [g.name, g.id]));
  for (const name of ['Sales', 'Managers']) {
    if (!have.has(name)) have.set(name, (await users.post('/groups', { name, description: `DA stress ${name}` })).id);
  }
  return have;
}

async function ensureRoles(users, groups) {
  const perm = (table, ...ops) => ops.map((op) => `dynamic-apps:crm:table:${table}:${op}`);
  const wanted = [
    { key: 'sales', name: 'Sales', bypass: false, group: 'Sales', permissions: [
      ...perm('companies', 'read'), ...perm('contacts', 'read'), ...perm('deals', 'read', 'create', 'update'),
      ...perm('activities', 'read', 'create', 'update', 'delete'), 'dynamic-apps:crm:flow:recalc_company:run'] },
    { key: 'managers', name: 'Managers', bypass: true, group: 'Managers', permissions: [
      ...perm('companies', 'read', 'update'), ...perm('contacts', 'read', 'update'), ...perm('deals', 'read', 'create', 'update', 'delete'),
      ...perm('activities', 'read', 'create', 'update', 'delete'), ...perm('tickets', 'read'), 'dynamic-apps:crm:flow:recalc_company:run'] },
  ];
  const existing = await users.get('/roles');
  for (const role of wanted) {
    let found = (existing.items ?? existing).find((r) => r.key === role.key);
    if (!found) {
      found = await users.post('/roles', { key: role.key, name: role.name, permissions: role.permissions, bypassRowAccess: role.bypass });
      await users.post(`/roles/${found.id}/grants`, { subjectType: 'group', subjectId: groups.get(role.group) });
    }
  }
}

async function ensureUsers(users, groups, siteBase, password) {
  const out = [];
  const existing = new Map(((await users.get('/users?pageSize=200')).items ?? []).map((u) => [u.email.toLowerCase(), u]));
  for (const u of USERS) {
    const email = emailOf(u.key);
    let inviteUrl;
    if (!existing.has(email.toLowerCase())) {
      const invited = await users.post('/users/invite', { email, displayName: `DA ${u.key}`, groups: u.group ? [groups.get(u.group)] : [] });
      inviteUrl = invited.inviteUrl;
    } else if (existing.get(email.toLowerCase()).status?.toLowerCase() === 'invited') {
      inviteUrl = (await users.post(`/users/${existing.get(email.toLowerCase()).id}/invite`)).inviteUrl;
    }
    const t = Date.now();
    const session = await realmSiteSession({ site: siteBase, email, password, inviteUrl });
    out.push({ ...u, email, cookie: session.cookie, groups: session.groups, signInMs: Date.now() - t });
  }
  log(`${out.length} site users signed in on ${siteBase} (median sign-in ${median(out.map((x) => x.signInMs))} ms)`);
  return out;
}

const median = (xs) => [...xs].sort((a, b) => a - b)[Math.floor(xs.length / 2)];

// ---------------------------------------------------------------- data

async function count(app, table) {
  return (await app.get(`/_records/${table}?pageSize=1&select=id`)).total;
}

/** Imports rows in calls of 1,000, timing each. Skips what the table already holds. */
async function importRows(app, table, target, make, timings) {
  const have = await count(app, table);
  if (have >= target) { log(`  ${table}: ${have} rows already`); return; }
  const calls = [];
  for (let from = have; from < target; from += 1000) {
    const rows = [];
    for (let i = from; i < Math.min(from + 1000, target); i++) rows.push(make(i));
    const t = Date.now();
    const result = await app.post(`/_records/${table}/import?dryRun=false`, { rows });
    const ms = Date.now() - t;
    if (result.errorCount) throw new Error(`${table} import at row ${from}: ${result.errorCount} errors, first ${JSON.stringify(result.errors.slice(0, 3))}`);
    calls.push({ from, rows: rows.length, ms, rowsPerSec: Math.round(rows.length / (ms / 1000)) });
    log(`  ${table}: ${from + rows.length}/${target} (${ms} ms, ${calls.at(-1).rowsPerSec} rows/s)`);
  }
  timings[table] = calls;
}

/** Every record id of a table, `select=id`, 200 a page. */
async function ids(app, table, limit = Infinity, extra = '') {
  const out = [];
  for (let page = 1; out.length < limit; page++) {
    const res = await app.get(`/_records/${table}?pageSize=200&page=${page}&select=id${extra}&sort=created_at`);
    out.push(...res.items.map((x) => x.id));
    if (res.items.length < 200) break;
  }
  return out.slice(0, limit);
}

async function seedData(app, scale, users) {
  const n = (x) => Math.max(10, Math.round(x * scale));
  const N = { companies: n(5000), profiles: n(2000), contacts: n(20000), products: n(500), deals: n(30000), activities: n(50000), tickets: n(5000) };
  const industries = ['software', 'manufacturing', 'retail', 'finance', 'health', 'energy', 'logistics', 'media'];
  const company = (i) => `Company ${pad(i)}`;
  const timings = {};

  await importRows(app, 'companies', N.companies, (i) => ({
    name: company(i), industry: industries[i % 8], tags: i % 5 === 0 ? ['vip', 'partner'] : i % 7 === 0 ? ['churn_risk'] : [],
    website: `https://c${i}.example.com`, employees: int(1, 50000), annual_revenue: int(10, 900000) * 100,
    founded: day(-int(365, 36500)), active: i % 13 !== 0, notes: `Notes for company ${i}. ${'Lorem ipsum dolor sit amet. '.repeat(i % 4)}`,
    metadata: { crmId: i, region: pick(['emea', 'na', 'apac']), scores: [int(0, 9), int(0, 9)] },
  }), timings);
  await importRows(app, 'company_profiles', N.profiles, (i) => ({
    headline: `Profile of ${company(i)}`, summary: 'A company profile. '.repeat(5), linkedin: `https://linkedin.example/c${i}`,
    hq_country: pick(['CZ', 'DE', 'US', 'GB', 'FR', 'PL']), company: company(i),
  }), timings);
  await importRows(app, 'contacts', N.contacts, (i) => ({
    email: `contact${pad(i)}@c${i % N.companies}.example.com`, first_name: pick(['Ada', 'Bob', 'Cy', 'Dana', 'Eve', 'Finn']),
    last_name: `Surname${pad(i)}`, phone: `+420 ${int(600000000, 799999999)}`, job_title: pick(['CEO', 'CTO', 'Buyer', 'Engineer', 'Ops']),
    birthday: day(-int(7000, 25000)), opted_in: i % 3 === 0, company: company(i % N.companies),
  }), timings);
  await importRows(app, 'products', N.products, (i) => ({
    sku: `SKU-${pad(i, 5)}`, name: `Product ${i}`, price: int(100, 99900) / 100, category: pick(['hardware', 'software', 'service', 'support']),
    active: i % 11 !== 0, cost: int(50, 50000) / 100,
  }), timings);
  await importRows(app, 'deals', N.deals, (i) => ({
    title: `D-${pad(i)} ${pick(['Renewal', 'Upsell', 'New logo', 'Pilot', 'Expansion'])}`, amount: int(500, 100000),
    stage: pick(['lead', 'qualified', 'proposal', 'negotiation', 'lost']), priority: pick(['low', 'normal', 'normal', 'high']),
    close_date: day(int(-200, 300)), probability: int(0, 100), internal_margin: int(5, 60),
    company: company(i % N.companies), primary_contact: `contact${pad(i % N.contacts)}@c${(i % N.contacts) % N.companies}.example.com`,
  }), timings);
  // Activities name their deal by title, which embeds its index: D-000123 ...
  const dealTitles = new Map();
  for (let page = 1; dealTitles.size < N.deals; page++) {
    const res = await app.get(`/_records/deals?pageSize=200&page=${page}&select=title,id`);
    for (const d of res.items) dealTitles.set(Number(d.title.slice(2, 8)), d.title);
    if (res.items.length < 200) break;
  }
  await importRows(app, 'activities', N.activities, (i) => {
    const deal = i % N.deals;
    return {
      subject: `Activity ${pad(i)}`, kind: pick(['call', 'email', 'meeting', 'note']), due_at: `${day(int(-100, 100))}T09:00:00Z`,
      done: i % 2 === 0, body: 'Discussed next steps. '.repeat(int(1, 6)), company: company(deal % N.companies), deal: dealTitles.get(deal),
    };
  }, timings);
  await importRows(app, 'tickets', N.tickets, (i) => ({
    subject: `Ticket ${pad(i)}`, description: 'Something is broken. '.repeat(3), priority: pick(['low', 'normal', 'high', 'urgent']),
    status: pick(['open', 'pending', 'resolved', 'closed']), reporter_email: `reporter${i}@example.com`,
    company: company(i % N.companies), internal_notes: 'internal',
  }), timings);

  // Row-access grants: salesNN holds every 8th of the first 1,600 companies; sales08 is "wide"
  // (every other company of all of them), guest01 holds 5, mgr* hold none (their role bypasses).
  const grants = [];
  for (const u of users) {
    if (u.key === 'sales08') for (let i = 0; i < N.companies; i += 2) grants.push({ user_email: u.email, company: company(i) });
    else if (u.key.startsWith('sales')) { const k = Number(u.key.slice(5)) - 1; for (let i = k; i < Math.min(N.companies, 1600); i += 8) grants.push({ user_email: u.email, company: company(i) }); }
    else if (u.key === 'guest01') for (let i = 0; i < 5; i++) grants.push({ user_email: u.email, company: company((i * 97) % N.companies) });
  }
  await importRows(app, 'company_access', grants.length, (i) => grants[i], timings);

  // Many-to-many: 3 products on each of the first n(1000) deals, one POST per link.
  const linked = (await app.get('/_records/products/' + (await ids(app, 'products', 1))[0] + '/deals?pageSize=1')).total;
  if (linked === 0) {
    const deals = await ids(app, 'deals', n(1000));
    const products = await ids(app, 'products');
    const links = deals.flatMap((d, i) => [0, 1, 2].map((k) => [d, products[(i * 3 + k) % products.length]]));
    const t = Date.now();
    await pool(links, 8, ([d, p]) => app.post(`/_records/deals/${d}/products`, { targetId: p }));
    timings.links = { count: links.length, ms: Date.now() - t, perSec: Math.round(links.length / ((Date.now() - t) / 1000)) };
    log(`  linked ${links.length} deal<->product pairs (${timings.links.perSec}/s at 8 in flight)`);
  }
  return { N, timings };
}

// ---------------------------------------------------------------- main

async function main() {
  const { env, overrides } = parseArgs(process.argv.slice(2));
  const profile = await loadProfile(env, { overrides });
  const t = targets(profile);
  const da = { tenant: 'da-stress', scale: 1, ...(profile.da ?? {}) };
  da.scale = Number(da.scale);

  const session = await edgeSession({ admin: t.admin, username: profile.auth.username, password: profile.auth.password });
  const root = new AdminApi({ base: t.admin, session });
  const tenants = await root.get('/api/admin/tenants');
  if (!tenants.some((x) => x.slug === da.tenant)) {
    await root.post('/api/admin/tenants', { slug: da.tenant, name: 'Dynamic Apps stress test' });
    log(`created tenant ${da.tenant}`);
  }
  const api = root.forTenant(da.tenant);

  const site = await ensureSite(api, da.tenant);
  const siteBase = `https://${site.domain}`;
  log(`site ${site.siteId} at ${siteBase}`);

  const crm = await ensureInstance(api, 'dynamic-apps', 'crm', 'Sales CRM', { displayName: 'DA stress CRM' });
  await ensureInstance(api, 'forms', 'contact', 'Contact forms',
    { forms: [{ name: 'lead', fields: [{ name: 'email', required: true }, { name: 'name' }, { name: 'message' }] }] });
  await ensureInstance(api, 'user-auth', 'users', 'Site users');

  const app = { get: (p, o) => api.get(`/api/admin/plugins/crm${p}`, o), post: (p, b, o) => api.post(`/api/admin/plugins/crm${p}`, b, o) };
  const users = { get: (p, o) => api.get(`/api/admin/plugins/users${p}`, o), post: (p, b, o) => api.post(`/api/admin/plugins/users${p}`, b, o),
    put: (p, b, o) => api.put(`/api/admin/plugins/users${p}`, b, o) };

  const { timings: modelTimings } = await ensureModel(app);

  const realm = await ensureRealm(users);
  log(`realm ${realm.slug} ready for ${realm.hosts.join(', ')}`);
  const groups = await ensureGroups(users);
  await ensureRoles(users, groups);
  await users.put(`/sites/${site.siteId}/rules`, [{ prefix: '/portal', access: 'signedIn', groups: [] }]);

  // The site users' password, kept from the first run: it is what their accounts now have.
  const secret = join(dirname(fixturesPath('x')), `da-${profile.name}-${da.tenant}.password`);
  const userPassword = process.env.DCMS_DA_USER_PASSWORD
    || await readFile(secret, 'utf8').then((s) => s.trim()).catch(() => null)
    || await (async () => { const p = `Da-${randomBytes(9).toString('base64url')}!`; await mkdir(dirname(secret), { recursive: true }); await writeFile(secret, p, { mode: 0o600 }); return p; })();
  const signedIn = await ensureUsers(users, groups, siteBase, userPassword);

  const { N, timings } = await seedData(app, da.scale, signedIn);
  const v2 = await ensureModelV2(app);
  await ensureFlowsCurrent(app);

  const sample = async (table) => ids(app, table, 200);
  const fixtures = {
    tenant: da.tenant, instanceSlug: 'crm', instanceId: crm.id, siteId: site.siteId, siteBase, domain: site.domain,
    userPassword,
    users: signedIn.map(({ key, group, email, cookie, groups: g }) => ({ key, group, email, cookie, groups: g })),
    counts: N,
    ids: { companies: await sample('companies'), deals: await sample('deals'), contacts: await sample('contacts'),
      products: await sample('products'), activities: await sample('activities'), tickets: await sample('tickets') },
    timings: { ...modelTimings, ...v2, imports: timings },
  };
  log(`wrote ${await writeFixtures(`da-${profile.name}${da.tenant === 'da-stress' ? '' : `-${da.tenant}`}`, fixtures)}`);
}

// Run only as a script: the probe imports pool() from here.
if (process.argv[1] === fileURLToPath(import.meta.url)) main().catch((error) => {
  console.error('[da-seed] FAILED:', error.message);
  if (error instanceof HttpError) console.error('[da-seed] at', error.path);
  if (process.env.DCMS_LOADTEST_DEBUG) console.error(error);
  process.exit(1);
});
