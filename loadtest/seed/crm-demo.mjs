#!/usr/bin/env node
// Builds the demo CRM in an existing tenant and fills it with data:
//
//   node loadtest/seed/crm-demo.mjs --env vps1                  # tenant test1
//   node loadtest/seed/crm-demo.mjs --env vps1 -e crm.tenant=x  # another tenant
//
// Dynamic Apps instance `crm` (model: crm-demo-model.mjs, data: crm-demo-data.mjs), a Forms
// instance `crm-forms` feeding two flows, and in the tenant's User Authentication instance
// seven CRM groups, five roles and nine demo site users. Then a short replay through the real
// APIs (site visitors, sales reps, customers, admins) so every automation has runs to show.
// Not a load test: it is kept for people to try. Idempotent: each step skips what is done.

import { loadProfile, parseArgs, targets } from '../lib/config.mjs';
import { edgeSession, realmSiteSession } from '../lib/auth.mjs';
import { AdminApi, HttpError } from '../lib/api.mjs';
import { writeFixtures, fixturesPath } from '../lib/fixtures.mjs';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { MODEL_V1, MODEL_V2, MODEL_V3, indexesWanted } from './crm-demo-model.mjs';
import { generate, IMPORT_ORDER, DEMO_USERS, demoEmail } from './crm-demo-data.mjs';
import { pool } from './dynamic-apps.mjs';

const log = (...args) => console.log('[crm]', ...args);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const INSTANCE_DESCRIPTION = 'Northwind Sales Cloud: a complete B2B CRM built with Dynamic Apps. Accounts, contacts, leads, opportunities with line items, '
  + 'quotes, contracts, invoices, campaigns, activities, support cases and a public knowledge base, with ~30 automations.';

const FORMS = { forms: [
  { name: 'demo-request', title: 'Request a demo', successMessage: 'Thanks! A specialist will contact you within one business day.', fields: [
    { name: 'name', type: 'text', label: 'Full name', required: true, maxLength: 160 },
    { name: 'email', type: 'email', label: 'Work email', required: true, maxLength: 200 },
    { name: 'company', type: 'text', label: 'Company', required: true, maxLength: 200 },
    { name: 'phone', type: 'text', label: 'Phone', maxLength: 40 },
    { name: 'seats', type: 'text', label: 'Number of users', maxLength: 10 },
    { name: 'message', type: 'textarea', label: 'What would you like to see?', maxLength: 4000 }] },
  { name: 'support-request', title: 'Contact support', successMessage: 'We received your request and opened a case.', fields: [
    { name: 'email', type: 'email', label: 'Email', required: true, maxLength: 200 },
    { name: 'subject', type: 'text', label: 'Subject', required: true, maxLength: 240 },
    { name: 'priority', type: 'text', label: 'Priority (low, normal, high, urgent)', maxLength: 10 },
    { name: 'message', type: 'textarea', label: 'Describe the problem', required: true, maxLength: 8000 }] },
] };

const GROUPS = {
  'CRM Sales EMEA': 'Sales reps in Europe, the Middle East and Africa: read every account of the EMEA territories.',
  'CRM Sales Americas': 'Sales reps in North and Latin America: read every account of the Americas territories.',
  'CRM Sales APAC': 'Sales reps in Asia-Pacific: read every account of the APAC territory.',
  'CRM Sales Managers': 'Sales managers: every CRM record, unrestricted.',
  'CRM Support': 'Support agents: every case, contact and account.',
  'CRM Partners': 'Reseller and implementation partners: the accounts whose team lists them.',
  'CRM Customers': 'Customers using the support portal: their own cases.',
};

const perm = (table, ...ops) => ops.map((op) => `dynamic-apps:crm:table:${table}:${op}`);
const flowRun = (...flows) => flows.map((f) => `dynamic-apps:crm:flow:${f}:run`);
const CRUD = ['read', 'create', 'update', 'delete'];
const ALL_TABLES = ['territories', 'accounts', 'account_team', 'contacts', 'leads', 'campaigns', 'products', 'competitors', 'opportunities', 'opportunity_line_items',
  'quotes', 'contracts', 'invoices', 'activities', 'cases', 'case_comments', 'knowledge_articles', 'sales_targets', 'audit_log', 'kpi_snapshots'];
const ROLES = [
  { key: 'crm_sales_rep', name: 'CRM sales rep', bypass: false, groups: ['CRM Sales EMEA', 'CRM Sales Americas', 'CRM Sales APAC'],
    description: 'Works their accounts: reads the territory, updates what their account team covers.',
    permissions: [...perm('accounts', 'read', 'update'), ...perm('contacts', 'read', 'create', 'update'), ...perm('leads', 'read', 'create', 'update'),
      ...perm('opportunities', 'read', 'create', 'update'), ...perm('opportunity_line_items', ...CRUD), ...perm('quotes', 'read', 'create', 'update'),
      ...perm('contracts', 'read'), ...perm('invoices', 'read'), ...perm('activities', ...CRUD), ...perm('cases', 'read'), ...perm('products', 'read'),
      ...perm('knowledge_articles', 'read'), ...flowRun('log_call', 'convert_lead', 'send_quote')] },
  { key: 'crm_sales_manager', name: 'CRM sales manager', bypass: true, groups: ['CRM Sales Managers', 'group-admin'],
    description: 'Every CRM table and flow, with row access rules bypassed.',
    permissions: [...ALL_TABLES.flatMap((t) => perm(t, ...CRUD)), ...flowRun('log_call', 'convert_lead', 'send_quote', 'escalate_case', 'onboard_account', 'pipeline_digest',
      'portal_invite', 'grant_partner_access', 'revoke_partner_access', 'adjust_lifetime_value', 'adjust_target')] },
  { key: 'crm_support', name: 'CRM support agent', bypass: true, groups: ['CRM Support'],
    description: 'Works every support case; reads accounts, contacts and products; edits the knowledge base.',
    permissions: [...perm('cases', 'read', 'create', 'update'), ...perm('case_comments', 'read', 'create', 'update'), ...perm('contacts', 'read', 'update'),
      ...perm('accounts', 'read'), ...perm('products', 'read'), ...perm('knowledge_articles', ...CRUD), ...flowRun('escalate_case', 'portal_invite')] },
  { key: 'crm_partner', name: 'CRM partner', bypass: false, groups: ['CRM Partners'],
    description: 'Reads the accounts, opportunities and contracts whose account team lists them.',
    permissions: [...perm('accounts', 'read'), ...perm('contacts', 'read'), ...perm('opportunities', 'read'), ...perm('contracts', 'read'), ...perm('products', 'read')] },
  { key: 'crm_customer', name: 'CRM customer', bypass: false, groups: ['CRM Customers'],
    description: 'Opens support cases, follows and answers their own.',
    permissions: [...perm('cases', 'read', 'create', 'update'), ...perm('case_comments', 'read', 'create'), ...perm('knowledge_articles', 'read'), ...perm('products', 'read')] },
];

// ---------------------------------------------------------------- helpers

/** Waits out the admin API's rate limit (600 requests a minute per client) instead of failing. */
async function patient(call) {
  for (let attempt = 0; ; attempt++) {
    try { return await call(); } catch (error) {
      if (!(error instanceof HttpError && error.status === 429) || attempt >= 30) throw error;
      await sleep(5000);
    }
  }
}

async function ensureInstance(api, pluginId, slug, name, description, config) {
  const found = (await api.get('/api/admin/plugins/instances')).find((x) => x.slug === slug);
  if (found) return found;
  const created = await api.post('/api/admin/plugins/instances', { pluginId, slug, name, description, config: JSON.stringify(config) });
  await api.post(`/api/admin/plugins/instances/${created.id}/enable`, undefined, { expect: [200, 204, 409] });
  log(`installed ${pluginId} as /${slug}`);
  return created;
}

async function publish(app, ops, description) {
  const state = await app.get('/_model');
  const applied = await app.post('/_model/draft/changes', { expectedHash: state.hash, operations: ops, description });
  for (const i of applied.issues) log(`  ${i.severity} ${i.code} ${i.path}: ${i.message}`);
  if (applied.issues.some((i) => i.severity === 'error')) throw new Error(`${description}: validation errors, see above.`);
  const t = Date.now();
  const published = await app.post('/_model/draft/publish', { expectedHash: applied.draft.hash });
  if (published.published === false) throw new Error(`${description}: refused: ${JSON.stringify(published.issues)}`);
  log(`published "${description}" as revision ${published.revision?.number} in ${Date.now() - t} ms`);
  return published;
}

/**
 * Records in a table. Totals stop at 5,000 (totalCapped), so past that the page where the
 * expected last row would sit says how many there are.
 */
async function count(app, table, expected = 0) {
  const first = await app.get(`/_records/${table}?pageSize=1&select=id`);
  if (!first.totalCapped) return first.total;
  const page = Math.max(1, Math.ceil(expected / 200));
  const at = await app.get(`/_records/${table}?pageSize=200&page=${page}&select=id&sort=created_at`);
  const more = at.items.length === 200 ? (await app.get(`/_records/${table}?pageSize=200&page=${page + 1}&select=id&sort=created_at`)).items.length : 0;
  return at.items.length ? (page - 1) * 200 + at.items.length + more : first.total;
}

async function importRows(app, table, rows) {
  const have = await count(app, table, rows.length);
  if (have >= rows.length) { log(`  ${table}: ${have} rows already`); return; }
  if (have > 0) throw new Error(`${table} holds ${have} of ${rows.length} rows: a partial import; delete them and run again.`);
  const t = Date.now();
  for (let from = 0; from < rows.length; from += 1000) {
    const batch = rows.slice(from, from + 1000);
    const result = await app.post(`/_records/${table}/import?dryRun=false`, { rows: batch });
    if (result.errorCount) throw new Error(`${table} import at row ${from}: ${result.errorCount} errors, first ${JSON.stringify(result.errors.slice(0, 3))}`);
  }
  log(`  ${table}: ${rows.length} rows in ${((Date.now() - t) / 1000).toFixed(1)} s`);
}

/** primary value (lower-cased) → id, for every record of a table. */
async function idsBy(app, table, key) {
  const out = new Map();
  for (let page = 1; ; page++) {
    const res = await app.get(`/_records/${table}?pageSize=200&page=${page}&select=id,${key}&sort=created_at`);
    for (const r of res.items) out.set(String(r[key]).toLowerCase(), r.id);
    if (res.items.length < 200) return out;
  }
}

async function ids(app, table, query) {
  return (await app.post(`/_records/${table}/query`, { pageSize: 200, ...query })).items.map((x) => x.id);
}

async function link(app, pairs, [table, key, nav], [otherTable, otherKey]) {
  const from = await idsBy(app, table, key), to = await idsBy(app, otherTable, otherKey);
  // Linking twice is not an error, so a partial run is simply repeated; the last pair says it finished.
  const last = from.get(pairs.at(-1)[0].toLowerCase());
  if ((await app.get(`/_records/${table}/${last}/${nav}?pageSize=200&select=id`)).items.some((x) => x.id === to.get(pairs.at(-1)[1].toLowerCase()))) {
    log(`  ${table}.${nav}: linked already`); return;
  }
  const t = Date.now();
  await pool(pairs, 4, ([a, b]) => app.post(`/_records/${table}/${from.get(a.toLowerCase())}/${nav}`, { targetId: to.get(b.toLowerCase()) }));
  log(`  ${table}.${nav}: ${pairs.length} links in ${((Date.now() - t) / 1000).toFixed(1)} s`);
}

async function drain(app, label) {
  for (let i = 0; i < 180; i++) {
    const waiting = (await app.get('/_automation/runs?status=pending&pageSize=1')).total + (await app.get('/_automation/runs?status=running&pageSize=1')).total;
    if (waiting === 0) { log(`automations drained (${label})`); return; }
    if (i % 6 === 0) log(`  ${waiting} runs still queued or running (${label})`);
    await sleep(5000);
  }
  log(`automations still busy after 15 min (${label}); moving on`);
}

/** Sorted-key JSON without nulls, so a flow compares equal however the server writes it. */
const canonical = (v) => JSON.stringify(v, (_, x) => (x === null ? undefined
  : x && typeof x === 'object' && !Array.isArray(x) ? Object.fromEntries(Object.entries(x).sort(([a], [b]) => a.localeCompare(b))) : x));

/** Brings the live flows up to MODEL_V2: changed steps, conditions or descriptions, and new flows. */
async function ensureFlowsCurrent(app) {
  const live = new Map((await app.get('/_model/published')).config.flows.map((f) => [f.apiName, f]));
  const ops = MODEL_V2.filter((o) => o.type === 'flow').flatMap((o) => {
    const have = live.get(o.value.apiName);
    if (!have) return [o];
    const want = { steps: o.value.steps, condition: o.value.condition ?? null, description: o.value.description };
    return canonical(want) === canonical({ steps: have.steps, condition: have.condition ?? null, description: have.description }) ? []
      : [{ op: 'update', type: 'flow', target: o.value.apiName, value: want }];
  });
  if (ops.length) await publish(app, ops, `Northwind CRM: flows fixed (${ops.map((o) => o.target ?? o.value.apiName).join(', ')})`);
}

// ---------------------------------------------------------------- users and access

async function ensureGroups(users) {
  const have = new Map((await users.get('/groups')).map((g) => [g.name, g.id]));
  for (const [name, description] of Object.entries(GROUPS)) {
    if (!have.has(name)) have.set(name, (await users.post('/groups', { name, description })).id);
  }
  return have;
}

async function ensureRoles(users, groups) {
  const existing = await users.get('/roles');
  for (const role of ROLES) {
    const found = (existing.items ?? existing).find((r) => r.key === role.key);
    if (found) {
      if ([...found.permissions].sort().join() !== [...role.permissions].sort().join()) {
        await users.put(`/roles/${found.id}`, { key: role.key, name: role.name, description: role.description, permissions: role.permissions, bypassRowAccess: role.bypass });
        log(`role ${role.key} permissions updated`);
      }
      continue;
    }
    const created = await users.post('/roles', { key: role.key, name: role.name, description: role.description, permissions: role.permissions, bypassRowAccess: role.bypass });
    for (const g of role.groups) if (groups.has(g)) await users.post(`/roles/${created.id}/grants`, { subjectType: 'group', subjectId: groups.get(g) });
    log(`role ${role.key} → ${role.groups.filter((g) => groups.has(g)).join(', ')}`);
  }
}

async function ensureUsers(users, groups, siteBase, password) {
  const existing = new Map(((await users.get('/users?pageSize=200')).items ?? []).map((u) => [u.email.toLowerCase(), u]));
  const out = {};
  for (const u of DEMO_USERS) {
    const email = demoEmail(u.key);
    let inviteUrl;
    const found = existing.get(email.toLowerCase());
    if (!found) inviteUrl = (await users.post('/users/invite', { email, displayName: u.name, groups: [groups.get(u.group)] })).inviteUrl;
    else if (found.status?.toLowerCase() === 'invited') inviteUrl = (await users.post(`/users/${found.id}/invite`)).inviteUrl;
    const session = await realmSiteSession({ site: siteBase, email, password, inviteUrl });
    out[u.key] = { ...u, email, cookie: session.cookie };
  }
  log(`${Object.keys(out).length} demo site users signed in on ${siteBase}`);
  return out;
}

// ---------------------------------------------------------------- replay: real use through the APIs

async function replay(app, site, people, profile) {
  const done = async (message) => (await app.post('/_records/audit_log/query', { filter: { and: [{ field: 'category', op: 'eq', value: 'demo' },
    { field: 'message', op: 'startsWith', value: message }] }, pageSize: 1 })).total > 0;
  const expectOk = (r, what) => { if (r.status >= 300) log(`  ! ${what}: ${r.status} ${JSON.stringify(r.body).slice(0, 300)}`); return r.body; };

  if (await done('Demo replay: activity')) log('replay of activity done already');
  else {
  // Site visitors: web-to-lead, one of them spam, and the two forms.
  const campaigns = await idsBy(app, 'campaigns', 'name');
  for (const [i, who] of ['Hana Králová', 'Peter Lindgren', 'Maya Goldberg', 'Arash Karimi', 'Julia Weiss'].entries()) {
    expectOk(await site('POST', '/api/crm/data/leads', { name: who, email: `${who.split(' ')[0].toLowerCase()}@prospect${i}.example`, company_name: `Prospect ${i} Group`,
      source: 'website', estimated_value: [12000, 85000, 30000, 140000, 6000][i], message: 'Saw the webinar, would like pricing.',
      campaign: [...campaigns.values()][40 + i] }), 'web lead');
  }
  expectOk(await site('POST', '/api/crm/data/leads', { name: 'Win Big', email: 'promo@mailinator.com', source: 'website', message: 'Cheap backlinks http://spam.example' }), 'spam lead');
  for (const [i, who] of ['Lena Fischer', 'Owen Price', 'Sara Conti'].entries()) {
    expectOk(await site('POST', '/api/crm-forms/forms/demo-request', { name: who, email: `${who.split(' ')[0].toLowerCase()}@formlead${i}.example`, company: `Form Lead ${i} AG`, seats: String(20 + i * 35), message: 'Demo please' }), 'demo form');
  }
  const someContact = (await app.get('/_records/contacts?pageSize=1&select=email&sort=created_at')).items[0].email;
  expectOk(await site('POST', '/api/crm-forms/forms/support-request', { email: someContact, subject: 'Cannot export report to Excel', priority: 'high', message: 'The export button does nothing.' }), 'support form');
  expectOk(await site('POST', '/api/crm-forms/forms/support-request', { email: 'unknown.person@nowhere.example', subject: 'Question about pricing', message: 'Do you offer NGO discounts?' }), 'support form');

  // A sales rep on the site: their accounts, an update, a call logged through a flow, a task.
  const mine = (await site('GET', '/api/crm/data/accounts?pageSize=20&sort=name', undefined, 'emea1')).body;
  log(`  emea1 sees ${mine.total} accounts on the site`);
  const team = (await app.post('/_records/account_team/query', { filter: { field: 'user_email', op: 'eq', value: demoEmail('emea1') }, pageSize: 3 })).items;
  for (const t of team) {
    expectOk(await site('PATCH', `/api/crm/data/accounts/${t.account}`, { health_score: 77 }, 'emea1'), 'rep updates account');
    expectOk(await site('POST', '/api/crm/flows/log_call/run', { input: { account: t.account, summary: 'Quarterly check-in, renewal looks likely', outcome: 'positive' } }, 'emea1'), 'rep logs a call');
    expectOk(await site('POST', '/api/crm/data/activities', { subject: 'Send renewal proposal', type: 'task', account: t.account, assignee_email: demoEmail('emea1'), due_at: '2026-10-20T09:00:00Z' }, 'emea1'), 'rep creates task');
  }

  // Customers on the portal: cases and replies.
  for (const [who, subject, priority] of [['customer1', 'Dashboards are empty since this morning', 'urgent'], ['customer1', 'How do I add a new user?', 'low'], ['customer2', 'Invoice address is wrong', 'normal']]) {
    const c = expectOk(await site('POST', '/api/crm/data/cases', { subject, priority, category: priority === 'urgent' ? 'outage' : 'question', description: `${subject}. Please help.`, reporter_email: demoEmail(who) }, who), 'customer case');
    if (c?.id) expectOk(await site('POST', '/api/crm/data/case_comments', { case: c.id, author_email: demoEmail(who), body: 'Adding a screenshot later today.' }, who), 'customer comment');
  }

  // Admins: the pipeline moves.
  const negotiating = await ids(app, 'opportunities', { filter: { field: 'stage', op: 'eq', value: 'negotiation' }, sort: [{ field: 'amount', direction: 'desc' }] });
  await app.post('/_records/opportunities/bulk-update', { ids: negotiating.slice(0, 40), values: { stage: 'closed_won' } });
  await app.post('/_records/opportunities/bulk-update', { ids: negotiating.slice(40, 65), values: { stage: 'closed_lost', loss_reason: 'competitor' } });
  const proposals = await ids(app, 'opportunities', { filter: { field: 'stage', op: 'eq', value: 'proposal' } });
  await app.post('/_records/opportunities/bulk-update', { ids: proposals.slice(0, 30), values: { probability: 60 } });
  const sentQuotes = await ids(app, 'quotes', { filter: { field: 'status', op: 'eq', value: 'sent' } });
  await app.post('/_records/quotes/bulk-update', { ids: sentQuotes.slice(0, 12), values: { status: 'accepted' } });
  const draftQuotes = await ids(app, 'quotes', { filter: { field: 'status', op: 'eq', value: 'draft' } });
  await app.post('/_records/quotes/bulk-update', { ids: draftQuotes.slice(1, 11), values: { status: 'sent' } });
  log('  pipeline: 40 won, 25 lost, 30 re-weighed, 12 quotes accepted, 10 sent');

  // Line items: add, change, remove.
  const open = await ids(app, 'opportunities', { filter: { field: 'stage', op: 'eq', value: 'qualification' } });
  const products = (await app.get('/_records/products?pageSize=40&select=id,name,list_price')).items;
  const added = [];
  for (const [i, opp] of open.slice(0, 20).entries()) {
    const p = products[i % products.length];
    added.push((await app.post('/_records/opportunity_line_items', { opportunity: opp, product: p.id, description: p.name, quantity: 10 + i, unit_price: p.list_price, discount_percent: i % 3 * 5 })).id);
  }
  await app.post('/_records/opportunity_line_items/bulk-update', { ids: added.slice(0, 5), values: { quantity: 50 } });
  await app.post('/_records/opportunity_line_items/bulk-delete', { ids: added.slice(17) });

  // Support, activities, billing, leads.
  const fresh = await ids(app, 'cases', { filter: { field: 'status', op: 'eq', value: 'new' } });
  await app.post('/_records/cases/bulk-update', { ids: fresh.slice(0, 30), values: { status: 'open' } });
  const working = await ids(app, 'cases', { filter: { field: 'status', op: 'eq', value: 'open' } });
  await app.post('/_records/cases/bulk-update', { ids: working.slice(0, 20), values: { status: 'resolved' } });
  for (const id of working.slice(30, 32)) await app.post('/_automation/flows/escalate_case/run', { input: { case: id, reason: 'Customer is a strategic account' } });
  const planned = await ids(app, 'activities', { filter: { and: [{ field: 'status', op: 'eq', value: 'planned' }, { field: 'due_at', op: 'lt', value: '2026-10-20T00:00:00Z' }] } });
  await app.post('/_records/activities/bulk-update', { ids: planned.slice(0, 50), values: { status: 'completed', outcome: 'positive' } });
  const issued = await ids(app, 'invoices', { filter: { field: 'status', op: 'eq', value: 'issued' } });
  await app.post('/_records/invoices/bulk-update', { ids: issued.slice(0, 30), values: { status: 'paid' } });
  const qualified = await ids(app, 'leads', { filter: { and: [{ field: 'status', op: 'eq', value: 'qualified' }, { field: 'converted_contact', op: 'isNull', value: true }] } });
  for (const id of qualified.slice(0, 15)) await app.post('/_automation/flows/convert_lead/run', { input: { lead: id } });
  log('  support: 30 cases opened, 20 resolved, 2 escalated; 50 activities completed; 30 invoices paid; 15 leads converted');

  // Relations: stakeholders join and leave.
  const contacts = (await app.get('/_records/contacts?pageSize=5&select=id')).items;
  for (const [i, opp] of open.slice(0, 5).entries()) await app.post(`/_records/opportunities/${opp}/stakeholders`, { targetId: contacts[i].id });
  await app.delete(`/_records/opportunities/${open[0]}/stakeholders/${contacts[0].id}`);

  await app.post('/_records/audit_log', { message: 'Demo replay: activity', category: 'demo', level: 'info' });
  }
  await drain(app, 'replay');
  if (await done('Demo replay complete')) { log('replay done already'); return; }
  // Contracts the accepted quotes drafted go live, which issues their first invoices.
  const drafts = await ids(app, 'contracts', { filter: { field: 'status', op: 'eq', value: 'draft' } });
  await app.post('/_records/contracts/bulk-update', { ids: drafts, values: { status: 'active', signed_by: 'Demo signatory' } });
  log(`  ${drafts.length} drafted contracts activated`);

  // Email and access flows, addressed to the tenant's own mailbox only.
  const quote = (await ids(app, 'quotes', { filter: { field: 'status', op: 'eq', value: 'draft' } }))[0];
  if (quote) await app.post('/_automation/flows/send_quote/run', { input: { quote, to: 'test+crm-quotes@highgeek.eu' } });
  await app.post('/_automation/flows/pipeline_digest/run', { input: { to: demoEmail('manager1') } });
  await app.post('/_automation/flows/portal_invite/run', { input: { email: demoEmail('customer3'), name: 'Jordan Demo (Customer)' } });
  await app.post('/_automation/flows/grant_partner_access/run', { input: { email: demoEmail('partner1') } });
  await app.post('/_records/audit_log', { message: `Demo replay complete (${profile.name})`, category: 'demo', level: 'info' });
  await drain(app, 'contracts and email');
}

// ---------------------------------------------------------------- main

async function main() {
  const { env, overrides } = parseArgs(process.argv.slice(2));
  const profile = await loadProfile(env, { overrides });
  const t = targets(profile);
  const tenant = profile.crm?.tenant ?? 'test1';
  const started = Date.now();

  const session = await edgeSession({ admin: t.admin, username: profile.auth.username, password: profile.auth.password });
  const api = new AdminApi({ base: t.admin, session }).forTenant(tenant);
  const instances = await api.get('/api/admin/plugins/instances');
  const authSlug = instances.find((x) => x.pluginId === 'user-auth')?.slug;
  if (!authSlug) throw new Error(`Tenant ${tenant} has no User Authentication instance.`);

  const crm = await ensureInstance(api, 'dynamic-apps', 'crm', 'Northwind CRM', INSTANCE_DESCRIPTION,
    { displayName: 'Northwind CRM', defaultLocale: 'en', defaultTimeZone: 'Europe/Prague' });
  if (!crm.aiToolsEnabled) await api.put(`/api/admin/plugins/instances/${crm.id}`, { aiToolsEnabled: true });
  await ensureInstance(api, 'forms', 'crm-forms', 'CRM forms', 'Demo request and support request forms; submissions become CRM leads and cases.', FORMS);

  const app = { get: (p, o) => patient(() => api.get(`/api/admin/plugins/crm${p}`, o)), post: (p, b, o) => patient(() => api.post(`/api/admin/plugins/crm${p}`, b, o)),
    delete: (p, o) => patient(() => api.delete(`/api/admin/plugins/crm${p}`, o)) };
  const users = { get: (p, o) => api.get(`/api/admin/plugins/${authSlug}${p}`, o), post: (p, b, o) => api.post(`/api/admin/plugins/${authSlug}${p}`, b, o),
    put: (p, b, o) => api.put(`/api/admin/plugins/${authSlug}${p}`, b, o) };

  const realm = await users.get('/realm');
  const host = realm.hosts.find((h) => !h.endsWith('.dcms.highgeek.eu')) ?? realm.hosts[0];
  const siteBase = `https://${host}`;
  const groups = await ensureGroups(users);
  log(`realm ${realm.slug} on ${siteBase}; ${groups.size} groups`);

  // V1: the data model, then the data.
  log(`model v1 asks for ${indexesWanted(MODEL_V1)} database indexes`);
  if (!(await app.get('/_model')).published) await publish(app, MODEL_V1, 'Northwind CRM v1: data model');
  const { tables, links } = generate({ groupIdsByName: Object.fromEntries(groups) });
  log(`importing ${Object.values(tables).reduce((s, r) => s + r.length, 0)} rows`);
  for (const table of IMPORT_ORDER) await importRows(app, table, tables[table]);
  log('linking many-to-many');
  await link(app, links.members, ['campaigns', 'name', 'members'], ['contacts', 'email']);
  await link(app, links.stakeholders, ['opportunities', 'name', 'stakeholders'], ['contacts', 'email']);
  await link(app, links.competitors, ['opportunities', 'name', 'competitors'], ['competitors', 'name']);
  await link(app, links.articleProducts, ['knowledge_articles', 'title', 'products'], ['products', 'sku']);

  // V2: the automations take over the computed fields.
  const live = await app.get('/_model/published');
  if (!live.config.flows.some((f) => f.apiName === 'opportunity_won')) await publish(app, MODEL_V2, 'Northwind CRM v2: automations');
  else await ensureFlowsCurrent(app);
  await ensureRoles(users, groups);

  const secret = join(dirname(fixturesPath('x')), `crm-${profile.name}-${tenant}.password`);
  const password = await readFile(secret, 'utf8').then((s) => s.trim()).catch(() => null)
    || await (async () => { const p = `Crm-${randomBytes(9).toString('base64url')}!`; await mkdir(dirname(secret), { recursive: true }); await writeFile(secret, p, { mode: 0o600 }); return p; })();
  const people = await ensureUsers(users, groups, siteBase, password);

  const site = async (method, path, body, who) => {
    const res = await fetch(`${siteBase}${path}`, { method, headers: { ...(body ? { 'content-type': 'application/json' } : {}), ...(who ? { cookie: people[who].cookie } : {}), origin: siteBase },
      body: body ? JSON.stringify(body) : undefined });
    return { status: res.status, body: await res.json().catch(() => null) };
  };
  await replay(app, site, people, profile);

  // V3: a schema change over the live data.
  const v2 = await app.get('/_model/published');
  if (!v2.config.tables.find((x) => x.apiName === 'accounts').fields.some((x) => x.apiName === 'account_rating')) {
    const published = await publish(app, MODEL_V3, 'Northwind CRM v3: account rating choice, fax deprecated');
    if (published.carried) log(`  carried values: ${JSON.stringify(published.carried).slice(0, 400)}`);
  }
  await drain(app, 'v3');

  // What is there.
  const counts = {};
  for (const table of [...IMPORT_ORDER, 'audit_log']) counts[table] = await count(app, table, tables[table]?.length ?? 0);
  const runs = {};
  for (const status of ['succeeded', 'skipped', 'failed', 'pending', 'running']) runs[status] = (await app.get(`/_automation/runs?status=${status}&pageSize=1`)).total;
  const openapi = await (await fetch(`${siteBase}/api/openapi.json`)).json();
  const paths = Object.keys(openapi.paths ?? {}).filter((p) => p.startsWith('/api/crm/'));
  log('records', counts);
  log('flow runs', runs);
  log(`OpenAPI: ${paths.length} /api/crm paths at ${siteBase}/api/openapi.json`);
  log(`done in ${((Date.now() - started) / 60000).toFixed(1)} min`);

  await writeFixtures(`crm-${profile.name}-${tenant}`, {
    tenant, siteBase, instanceId: crm.id, password, counts, runs,
    users: Object.values(people).map(({ key, email, group }) => ({ key, email, group })),
  });
}

main().catch((error) => {
  console.error('[crm] FAILED:', error.message);
  if (error instanceof HttpError) console.error('[crm] at', error.path);
  if (process.env.DCMS_LOADTEST_DEBUG) console.error(error);
  process.exit(1);
});
