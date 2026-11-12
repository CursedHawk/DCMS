/**
 * The declarations a Mode B site is type-checked against, without Monaco.
 *
 * <p>`virtual:dcms-palette-types` is produced at build time by vite-plugin-palette-types: real
 * .d.ts for React, plus ambient stubs for the rest of the dependency palette so their bare
 * imports resolve to `any` rather than "cannot find module". The editor hands these to Monaco
 * (`palette.ts`); an agent run hands the same list to its own checker (`agent/runChecks.ts`), so
 * both answer the same question.</p>
 */

/** Global ambient declarations for the Vite-isms a Mode B site uses. */
export const AMBIENT_EXTRA = `
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

export const AMBIENT_PATH = 'file:///node_modules/@dcms-ambient/index.d.ts';

export interface PaletteLib {
  path: string;
  content: string;
}

let cached: Promise<PaletteLib[]> | null = null;

/** The palette's libraries and the ambient stub, loaded once. */
export function paletteLibs(): Promise<PaletteLib[]> {
  cached ??= (async () => {
    try {
      const mod = (await import('virtual:dcms-palette-types')) as {
        libs: PaletteLib[];
        ambientModules: string[];
      };
      const stubs = (mod.ambientModules ?? [])
        .map((m) => `declare module '${m}';\ndeclare module '${m}/*';`)
        .join('\n');
      return [...mod.libs, { path: AMBIENT_PATH, content: stubs + AMBIENT_EXTRA }];
    } catch (err) {
      // Looser types rather than none: the ambient stub still resolves the Vite-isms.
      console.warn('[ide] palette types unavailable; continuing with reduced type checking', err);
      return [{ path: AMBIENT_PATH, content: AMBIENT_EXTRA }];
    }
  })();
  return cached;
}
