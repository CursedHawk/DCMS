#!/usr/bin/env node
//
// ai-bench — folds IDE agent run records into a committed baseline and compares labelled sets.
//
// See README.md for what this measures and, more importantly, what it cannot. The short version:
// ai-gateway counts tokens per API CALL into Prometheus; this counts them per RUN, which is the
// unit the rework has to move. The app produces records, this script stores and compares them.
//
// No dependencies, by design: an instrument that needs an install is an instrument that stops
// being run.

import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const STORE = resolve(ROOT, 'benchmarks/ai-agent.json');

/** The fixed task set. Recording an id that is not here is refused: a benchmark whose tasks drift
 *  is a benchmark that can show any result you like. */
const TASKS = ['button-copy', 'add-page', 'fix-build'];

function loadStore() {
  if (!existsSync(STORE)) return { runs: [] };
  return JSON.parse(readFileSync(STORE, 'utf8'));
}

function saveStore(store) {
  mkdirSync(dirname(STORE), { recursive: true });
  writeFileSync(STORE, `${JSON.stringify(store, null, 2)}\n`);
}

function fail(message) {
  console.error(`ai-bench: ${message}`);
  process.exit(1);
}

function record(args) {
  const task = flag(args, '--task');
  const label = flag(args, '--label');
  const file = args.find((a) => !a.startsWith('--') && !TASKS.includes(a) && a !== task && a !== label);

  if (!task) fail('--task is required');
  if (!TASKS.includes(task)) fail(`unknown task "${task}". Known: ${TASKS.join(', ')}`);
  if (!label) fail('--label is required (e.g. --label before)');
  if (!file) fail('a run-record JSON file is required');
  if (!existsSync(file)) fail(`no such file: ${file}`);

  const run = JSON.parse(readFileSync(file, 'utf8'));

  if (run.outcome !== 'completed') {
    fail(
      `refusing to record a run whose outcome is "${run.outcome}". Its tokens were really spent, ` +
        'but a run that stopped early did less work and would flatter the comparison.',
    );
  }
  if (!run.model) {
    fail('run record has no model. A run that cannot name its model cannot be compared.');
  }

  const store = loadStore();
  store.runs.push({ ...run, task, label });
  saveStore(store);

  console.log(
    `recorded ${task} @ ${label}: ${fmt(run.inputTokens)} in / ${run.outputTokens} out, ` +
      `${run.turns} turns, ${run.toolCalls} tool calls, ${(run.wallMs / 1000).toFixed(1)}s ` +
      `(${run.model})`,
  );
}

function compare(args) {
  const before = flag(args, '--before');
  const after = flag(args, '--after');
  if (!before || !after) fail('--before and --after labels are both required');

  const store = loadStore();
  const pick = (label, task) => store.runs.find((r) => r.label === label && r.task === task);

  const rows = [];
  for (const task of TASKS) {
    const a = pick(before, task);
    const b = pick(after, task);
    if (!a || !b) {
      rows.push({ task, note: `missing a run (${!a ? before : after})` });
      continue;
    }
    if (a.model !== b.model) {
      // The single most misleading comparison available, so it is refused rather than footnoted.
      rows.push({ task, note: `model changed (${a.model} → ${b.model}) — not comparable` });
      continue;
    }
    rows.push({
      task,
      input: delta(a.inputTokens, b.inputTokens),
      output: delta(a.outputTokens, b.outputTokens),
      turns: delta(a.turns, b.turns),
      tools: delta(a.toolCalls, b.toolCalls),
      wall: delta(a.wallMs, b.wallMs),
    });
  }

  console.log(`\n${before} → ${after}\n`);
  for (const r of rows) {
    if (r.note) {
      console.log(`  ${r.task.padEnd(13)} ${r.note}`);
      continue;
    }
    console.log(
      `  ${r.task.padEnd(13)} input ${r.input.padEnd(18)} output ${r.output.padEnd(18)} ` +
        `turns ${r.turns.padEnd(14)} tools ${r.tools.padEnd(14)} wall ${r.wall}`,
    );
  }
  console.log(
    '\n  n=1 per task on a non-deterministic system. Treat anything under 20% as noise.\n',
  );
}

/** A before/after pair as "1200 → 800 (-33%)", or an honest refusal when either side is unknown. */
function delta(a, b) {
  if (a === null || b === null || a === undefined || b === undefined) return 'unknown';
  if (a === 0) return `${a} → ${b}`;
  const pct = Math.round(((b - a) / a) * 100);
  return `${a} → ${b} (${pct >= 0 ? '+' : ''}${pct}%)`;
}

const fmt = (v) => (v === null || v === undefined ? 'unknown' : String(v));

function flag(args, name) {
  const i = args.indexOf(name);
  return i === -1 ? null : args[i + 1];
}

const [command, ...args] = process.argv.slice(2);
if (command === 'record') record(args);
else if (command === 'compare') compare(args);
else {
  console.log('usage:\n  ai-bench record --task <id> --label <label> <run.json>\n  ai-bench compare --before <label> --after <label>');
  process.exit(command ? 1 : 0);
}
