// Fills each static route's HTML in dist/ with its prerendered markup (see src/entry-server.tsx),
// so the page's text is there before any script runs. The browser hydrates it.
//
// Best effort by design: a page that cannot be prerendered keeps its empty root and renders in
// the browser exactly as it did before prerendering existed — it never fails the build.
import { existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = process.cwd();
const ssr = join(root, '.ssr');
const EMPTY_ROOT = '<div id="root"></div>';

try {
  const { pages } = await import(pathToFileURL(join(ssr, 'entry-server.js')).href);
  for (const { path, fileName, html } of pages()) {
    const file = join(root, 'dist', fileName);
    if (!existsSync(file)) continue;
    const page = readFileSync(file, 'utf8');
    if (!page.includes(EMPTY_ROOT)) continue;
    writeFileSync(file, page.replace(EMPTY_ROOT, `<div id="root">${html}</div>`));
    console.log(`prerendered ${path}`);
  }
} catch (error) {
  console.warn(`dcms: pages were not prerendered: ${error instanceof Error ? error.message : error}`);
} finally {
  rmSync(ssr, { recursive: true, force: true });
}
