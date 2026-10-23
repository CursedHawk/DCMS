#!/usr/bin/env node
// CPU-ms per request, per container, for a routecost run (scenarios/routecost.js):
//
//   node loadtest/collect/routecost.mjs loadtest/runs/<run>
//
// Rebuilds each route's window from the ROUTECOST_PLAN / ROUTECOST_T0 lines in k6.log, reads every
// container's CPU-seconds at the window's edges from cpusample.txt (collect/cpusample.sh, which
// run.sh records for this scenario), subtracts the idle window's rate, and divides by the requests
// the window carried. HOST is all non-idle CPU, kernel and softirq included -- TLS and the network
// stack live there, outside every container. The first 15 s of each window are skipped (connection
// set-up, JIT). Writes routecost.txt and routecost.json next to the input.

import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const [dir] = process.argv.slice(2);
if (!dir) { console.error('usage: routecost.mjs <run-dir>'); process.exit(2); }
const SKIP = 15;

// k6 logs console.log as msg="..." with the quotes inside escaped.
const log = readFileSync(join(dir, 'k6.log'), 'utf8');
const grab = (tag) => log.match(new RegExp(`${tag} ((?:\\\\"|[^"\\n])*)`))?.[1]?.replace(/\\"/g, '"');
const plan = JSON.parse(grab('ROUTECOST_PLAN') ?? 'null');
const t0 = Number(grab('ROUTECOST_T0')) / 1000;
if (!plan || !t0) { console.error('no ROUTECOST_PLAN / ROUTECOST_T0 in k6.log -- not a routecost run?'); process.exit(1); }

// name -> [[epoch, cpu-seconds], ...]
const series = new Map();
for (const line of readFileSync(join(dir, 'cpusample.txt'), 'utf8').split('\n')) {
  const [t, name, v] = line.split(' ');
  if (!v) continue;
  const key = name.replace(/^dcms-|-1$/g, '');
  if (!series.has(key)) series.set(key, []);
  series.get(key).push([Number(t), Number(v)]);
}
// Linear between the two samples around `when`; undefined when the container was not running then.
const at = (s, when) => {
  const i = s.findIndex(([t]) => t >= when);
  if (i <= 0) return i === 0 && s[0][0] === when ? s[0][1] : undefined;
  const [t1, v1] = s[i - 1], [t2, v2] = s[i];
  return v1 + ((v2 - v1) * (when - t1)) / (t2 - t1);
};
const used = (from, to) => Object.fromEntries([...series].map(([n, s]) => [n, at(s, to) - at(s, from)]).filter(([, v]) => Number.isFinite(v)));

const idleFrom = t0 + SKIP, idleTo = t0 + plan[0].start;
const idle = used(idleFrom, idleTo);
const rows = plan.map((p) => {
  const from = t0 + p.start + SKIP, to = t0 + p.start + p.seconds;
  const requests = p.rate * (to - from);
  const perReq = Object.fromEntries(Object.entries(used(from, to)).map(([n, s]) =>
    [n, ((s - ((idle[n] ?? 0) * (to - from)) / (idleTo - idleFrom)) * 1000) / requests]));
  return { route: p.name, rate: p.rate, requests, perReq };
});

// Columns: containers costing over 0.3 ms per request on some route, the costliest first.
const total = (n) => rows.reduce((s, r) => s + (r.perReq[n] ?? 0), 0);
const cols = [...new Set(rows.flatMap((r) => Object.keys(r.perReq).filter((n) => n !== 'HOST' && r.perReq[n] > 0.3)))]
  .sort((a, b) => total(b) - total(a));
const cell = (v, w) => (v === undefined ? '' : v.toFixed(1)).padStart(w);
const lines = [
  `CPU-ms per request, idle subtracted (idle host: ${(((idle.HOST ?? 0) / (idleTo - idleFrom)) * 100).toFixed(0)} % of one core). t0=${new Date(t0 * 1000).toISOString()}`,
  ['route'.padEnd(20), 'req/s'.padStart(5), 'HOST'.padStart(7), 'Σctr'.padStart(7), ...cols.map((c) => c.slice(0, 10).padStart(11))].join(' '),
  ...rows.sort((a, b) => (b.perReq.HOST ?? 0) - (a.perReq.HOST ?? 0)).map((r) => [
    r.route.padEnd(20), String(r.rate).padStart(5), cell(r.perReq.HOST, 7),
    cell(Object.entries(r.perReq).filter(([n]) => n !== 'HOST').reduce((s, [, v]) => s + v, 0), 7),
    ...cols.map((c) => cell(r.perReq[c], 11))].join(' ')),
];
writeFileSync(join(dir, 'routecost.txt'), `${lines.join('\n')}\n`);
writeFileSync(join(dir, 'routecost.json'), JSON.stringify({ t0, idle, rows }, null, 2));
console.log(lines.join('\n'));
