import type * as Monaco from 'monaco-editor';

// Automatic type acquisition for the fixed Mode B dependency palette. The
// `virtual:dcms-palette-types` module is produced at build/dev time by
// vite-plugin-palette-types: real .d.ts for React (so JSX IntelliSense is
// precise) plus ambient stubs for the rest of packages/site-builder-toolchain
// (so their bare imports resolve to `any` instead of "cannot find module").

// Global ambient declarations for the Vite-isms a Mode B site uses.
const AMBIENT_EXTRA = `
interface ImportMetaEnv { readonly [key: string]: string | undefined }
interface ImportMeta { readonly env: ImportMetaEnv }
declare module '*.css';
declare module '*.scss';
declare module '*.svg' { const src: string; export default src; }
declare module '*.png' { const src: string; export default src; }
declare module '*.jpg' { const src: string; export default src; }
declare module '*.webp' { const src: string; export default src; }
declare module '*.json' { const value: unknown; export default value; }
`;

export async function loadPaletteTypes(monaco: typeof Monaco): Promise<void> {
  const ts = monaco.languages.typescript;
  try {
    const mod = (await import('virtual:dcms-palette-types')) as {
      libs: { path: string; content: string }[];
      ambientModules: string[];
    };

    for (const lib of mod.libs) {
      ts.typescriptDefaults.addExtraLib(lib.content, lib.path);
      ts.javascriptDefaults.addExtraLib(lib.content, lib.path);
    }

    const stubs = (mod.ambientModules ?? [])
      .map((m) => `declare module '${m}';\ndeclare module '${m}/*';`)
      .join('\n');

    ts.typescriptDefaults.addExtraLib(
      stubs + AMBIENT_EXTRA,
      'file:///node_modules/@dcms-ambient/index.d.ts',
    );
  } catch (err) {
    // The IDE still works without the palette manifest — just with looser types.
    console.warn('[ide] palette types unavailable; continuing with reduced IntelliSense', err);
    ts.typescriptDefaults.addExtraLib(AMBIENT_EXTRA, 'file:///node_modules/@dcms-ambient/index.d.ts');
  }
}
