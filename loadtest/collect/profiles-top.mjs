#!/usr/bin/env node
// Summarises a bundle's profiles/<service>.json (Pyroscope flamebearer, one per service, for
// the run window) as profiles/top.txt: per service, the functions with the most SELF CPU time.
//
//   node loadtest/collect/profiles-top.mjs <run-dir> [top-n]
//
// Self time, not total: the total of `Main` is 100 % and says nothing. The function where the
// CPU was actually spent is the one worth reading -- then open the flamegraph in Grafana
// (Explore -> Profiles) for the path that led there.

import { readdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

const [dir, topArg] = process.argv.slice(2);
if (!dir) { console.error('usage: profiles-top.mjs <run-dir> [top-n]'); process.exit(2); }
const TOP = Number(topArg ?? 15);
const profiles = join(dir, 'profiles');

const out = [];
for (const file of (await readdir(profiles)).filter((f) => f.endsWith('.json')).sort()) {
  const service = file.replace(/\.json$/, '');
  let fb;
  try { fb = JSON.parse(await readFile(join(profiles, file), 'utf8')).flamebearer; } catch { fb = null; }
  if (!fb?.numTicks) { out.push(`== ${service}: no samples`, ''); continue; }

  // Flamebearer "single" format: each level is a flat array of [offset, total, self, name] quads.
  const self = new Map();
  for (const level of fb.levels) {
    for (let i = 0; i < level.length; i += 4) {
      if (level[i + 2] > 0) {
        const name = fb.names[level[i + 3]];
        self.set(name, (self.get(name) ?? 0) + level[i + 2]);
      }
    }
  }
  // process_cpu ticks are nanoseconds of CPU.
  out.push(`== ${service}: ${(fb.numTicks / 1e9).toFixed(1)} CPU-s in window`);
  for (const [name, ticks] of [...self].sort((a, b) => b[1] - a[1]).slice(0, TOP)) {
    out.push(`${(100 * ticks / fb.numTicks).toFixed(1).padStart(5)} %  ${(ticks / 1e9).toFixed(2).padStart(7)} s  ${name}`);
  }
  out.push('');
}
await writeFile(join(profiles, 'top.txt'), out.join('\n'));
console.log(`profiles/top.txt: ${out.filter((l) => l.startsWith('==')).length} services`);
