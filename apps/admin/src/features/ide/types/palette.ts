import type * as Monaco from 'monaco-editor';
import { setupMonaco } from '../../site-source/monaco-setup';
import { AMBIENT_PATH, paletteLibs } from './paletteLibs';

// Automatic type acquisition for the fixed Mode B dependency palette. The
// `virtual:dcms-palette-types` module is produced at build/dev time by
// vite-plugin-palette-types: real .d.ts for React (so JSX IntelliSense is
// precise) plus ambient stubs for the rest of packages/site-builder-toolchain
// (so their bare imports resolve to `any` instead of "cannot find module").

let seeded = false;

/**
 * Seed the palette typings once per session. The shared Monaco setup knows
 * nothing about Mode B, so the IDE opts in by calling this on mount; the
 * underlying `setupMonaco()` is idempotent, so calling it here is safe whether
 * or not an editor has mounted yet.
 */
export function ensurePaletteTypes(): void {
  if (seeded) return;
  seeded = true;
  void loadPaletteTypes(setupMonaco());
}

export async function loadPaletteTypes(monaco: typeof Monaco): Promise<void> {
  const ts = monaco.languages.typescript;
  for (const lib of await paletteLibs()) {
    ts.typescriptDefaults.addExtraLib(lib.content, lib.path);
    if (lib.path !== AMBIENT_PATH) ts.javascriptDefaults.addExtraLib(lib.content, lib.path);
  }
}
