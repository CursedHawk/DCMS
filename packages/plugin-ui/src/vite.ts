/**
 * Vite preset for a plugin's admin UI built OUTSIDE this repository (an installed plugin).
 *
 *   // vite.config.ts
 *   import { defineConfig } from 'vite';
 *   import { dcmsPluginAdmin } from '@dcms/plugin-ui/vite';
 *   export default defineConfig(dcmsPluginAdmin({ entry: 'src/index.tsx' }));
 *
 * Produces `dist/index.js` (one ES module, default export `definePluginAdmin({...})`) and
 * `dist/index.css` when the UI has styles. Publish both to `admin/` in the plugin's folder;
 * the console loads them from there.
 *
 * Shared modules (./shared.ts) are not bundled: each import of one becomes a read of the
 * console's copy from `globalThis.__DCMS_SHARED__`. Named imports work through Rollup's
 * synthetic named exports, so the plugin writes ordinary `import { Button } from '@dcms/ui'`.
 */
import type { Plugin, UserConfig } from 'vite';
// With its extension: Vite loads this file through Node, which resolves TypeScript source only by full name.
import { SHARED_GLOBAL, SHARED_MODULES } from './shared.ts';

const PREFIX = '\0dcms-shared:';

/** The Rollup plugin alone, for a config that sets up its own build. */
export function dcmsSharedModules(): Plugin {
  const shared = new Set<string>(SHARED_MODULES);
  return {
    name: 'dcms-shared-modules',
    enforce: 'pre',
    resolveId(id) {
      return shared.has(id) ? PREFIX + id : null;
    },
    load(id) {
      if (!id.startsWith(PREFIX)) return null;
      const name = JSON.stringify(id.slice(PREFIX.length));
      return {
        code: [
          `const registry = globalThis[${JSON.stringify(SHARED_GLOBAL)}];`,
          `const m = registry && registry[${name}];`,
          `if (!m) throw new Error("DCMS console did not provide " + ${name} + "; this plugin UI needs a newer console.");`,
          // The namespace object: named imports are read from it.
          `export const __ns = m;`,
          `export default m.default !== undefined ? m.default : m;`,
        ].join('\n'),
        syntheticNamedExports: '__ns',
      };
    },
  };
}

/** A complete config: library build of `entry` to dist/index.js (+ index.css), shared modules external. */
export function dcmsPluginAdmin(options: { entry: string; outDir?: string }): UserConfig {
  return {
    plugins: [dcmsSharedModules()],
    // The console's React is 19 with the automatic runtime; JSX compiles to react/jsx-runtime,
    // which is shared like the rest.
    esbuild: { jsx: 'automatic' },
    define: { 'process.env.NODE_ENV': JSON.stringify('production') },
    build: {
      outDir: options.outDir ?? 'dist',
      emptyOutDir: true,
      cssCodeSplit: false,
      lib: { entry: options.entry, formats: ['es'], fileName: () => 'index.js', cssFileName: 'index' },
      rollupOptions: {
        // One file: the console imports it by URL, and chunks would resolve against that URL
        // through an anonymous asset route — keep it simple and whole.
        output: { inlineDynamicImports: true },
      },
    },
  };
}
