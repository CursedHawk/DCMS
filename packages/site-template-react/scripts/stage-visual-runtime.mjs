// Copies @dcms/site-runtime's source to where a Mode D site receives it (src/dcms/runtime/),
// so `tsconfig.visual.json` typechecks templates/visual against exactly what DCMS ships —
// the same files admin-api embeds, minus the tests. The output (.visual/) is gitignored.
import { cpSync, mkdirSync, readdirSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const from = join(here, '../../site-runtime/src');
const to = join(here, '../.visual/src/dcms/runtime');
rmSync(join(here, '../.visual'), { recursive: true, force: true });
mkdirSync(to, { recursive: true });
for (const file of readdirSync(from)) {
  if (/\.test\.tsx?$/.test(file)) continue;
  cpSync(join(from, file), join(to, file));
}
