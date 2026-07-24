import { existsSync, readFileSync, readdirSync, realpathSync } from 'node:fs';
import { join, resolve } from 'node:path';
import type { Plugin } from 'vite';

// Emits a `virtual:dcms-palette-types` module for the IDE's in-browser
// TypeScript service (see src/features/ide/types/palette.ts):
//   - real .d.ts for React, harvested from the admin app's node_modules, so
//     JSX/hook IntelliSense is accurate;
//   - the names of every runtime dependency in the Mode B fixed palette
//     (packages/site-builder-toolchain), turned into ambient `declare module`
//     stubs so their bare imports resolve instead of erroring.
// Everything is best-effort: if a path is missing the module still loads (with
// looser types) rather than breaking the admin build.

const VIRTUAL_ID = 'virtual:dcms-palette-types';
const RESOLVED_ID = '\0' + VIRTUAL_ID;

// Packages we ship real typings for (excluded from the ambient stub list).
const REAL_TYPE_PACKAGES = ['@types/react', '@types/react-dom'];
const REAL_TYPED_MODULES = ['react', 'react-dom'];

interface Lib {
  path: string;
  content: string;
}

function collectDts(logicalBase: string, absBase: string, out: Lib[]): void {
  let realBase: string;
  try {
    realBase = realpathSync(absBase);
  } catch {
    return;
  }
  const walk = (relDir: string): void => {
    const dir = relDir ? join(realBase, relDir) : realBase;
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const rel = relDir ? `${relDir}/${entry.name}` : entry.name;
      if (entry.isDirectory()) {
        walk(rel);
      } else if (entry.name.endsWith('.d.ts')) {
        try {
          out.push({
            path: `file:///node_modules/${logicalBase}/${rel}`,
            content: readFileSync(join(realBase, rel), 'utf8'),
          });
        } catch {
          /* skip unreadable file */
        }
      }
    }
  };
  walk('');
}

function buildManifest(adminRoot: string): {
  libs: Lib[];
  ambientModules: string[];
  versions: Record<string, string>;
} {
  const libs: Lib[] = [];
  for (const pkg of REAL_TYPE_PACKAGES) {
    const abs = join(adminRoot, 'node_modules', ...pkg.split('/'));
    if (existsSync(abs)) collectDts(pkg, abs, libs);
  }

  let ambientModules: string[] = [];
  const versions: Record<string, string> = {};
  try {
    const toolchain = resolve(adminRoot, '../../packages/site-builder-toolchain/package.json');
    const deps: Record<string, string> = JSON.parse(readFileSync(toolchain, 'utf8')).dependencies ?? {};
    ambientModules = Object.keys(deps).filter((name) => !REAL_TYPED_MODULES.includes(name));
    for (const [name, range] of Object.entries(deps)) {
      versions[name] = String(range).replace(/^[\^~]/, '');
    }
  } catch {
    /* no toolchain manifest — ambient stubs / versions simply omitted */
  }

  return { libs, ambientModules, versions };
}

export function paletteTypesPlugin(): Plugin {
  let adminRoot = process.cwd();
  return {
    name: 'dcms-palette-types',
    configResolved(config) {
      adminRoot = config.root;
    },
    resolveId(id) {
      return id === VIRTUAL_ID ? RESOLVED_ID : null;
    },
    load(id) {
      if (id !== RESOLVED_ID) return null;
      const manifest = buildManifest(adminRoot);
      return `export const libs = ${JSON.stringify(manifest.libs)};
export const ambientModules = ${JSON.stringify(manifest.ambientModules)};
export const versions = ${JSON.stringify(manifest.versions)};`;
    },
  };
}
