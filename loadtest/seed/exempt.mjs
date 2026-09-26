#!/usr/bin/env node
// Adds, lists or removes a rate-limit exemption -- the console's Operations -> Rate limits page,
// scriptable so a campaign can bracket itself with add ... remove.
//
//   node loadtest/seed/exempt.mjs --env vps1 add 51.195.116.123 "load test"
//   node loadtest/seed/exempt.mjs --env vps1 list
//   node loadtest/seed/exempt.mjs --env vps1 remove 51.195.116.123
//
// Remove it when the run is over, then prove the limit is back with the ratelimit scenario
// (README, "The rate limiters"). The address is normalised server-side (a bare IP is a /32).

import { loadProfile, parseArgs, targets } from '../lib/config.mjs';
import { edgeSession } from '../lib/auth.mjs';
import { AdminApi } from '../lib/api.mjs';

const PATH = '/api/admin/platform/rate-limit-exemptions';
const { env, overrides, rest: [verb, address, note] } = parseArgs(process.argv.slice(2));
if (!['add', 'list', 'remove'].includes(verb) || (verb !== 'list' && !address)) {
  console.error('usage: exempt.mjs --env <env> add <ip|cidr> [note] | list | remove <ip|cidr>');
  process.exit(2);
}

const profile = await loadProfile(env, { overrides });
const t = targets(profile);
const session = await edgeSession({ admin: t.admin, username: profile.auth.username, password: profile.auth.password });
const api = new AdminApi({ base: t.admin, session });

// A bare address is stored as its /32 (or /128), so compare on the address part.
const matches = (row) => row.cidr === address || row.cidr.split('/')[0] === address;

if (verb === 'add') {
  const row = await api.post(PATH, { address, note: note ?? `loadtest ${new Date().toISOString()}` });
  console.log(`exempt: ${row.cidr}`);
} else if (verb === 'remove') {
  const rows = (await api.get(PATH)).filter(matches);
  for (const row of rows) await api.delete(`${PATH}/${row.id}`);
  console.log(rows.length ? `removed: ${rows.map((r) => r.cidr).join(', ')}` : `no exemption for ${address}`);
} else {
  for (const row of await api.get(PATH)) console.log(`${row.cidr}\t${row.note}\t${row.createdBy}`);
}
