/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_OIDC_AUTHORITY?: string;
  readonly VITE_OIDC_CLIENT_ID?: string;
  readonly VITE_ADMIN_API_BASE?: string;
  readonly VITE_CONTENT_API_BASE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}

// Palette type manifest produced by vite-plugin-palette-types (IDE ATA + preview).
declare module 'virtual:dcms-palette-types' {
  export const libs: { path: string; content: string }[];
  export const ambientModules: string[];
  export const versions: Record<string, string>;
}

// Monaco internals the editor builds on (monaco-setup.ts, ts.worker.ts); they ship without types.
declare module 'monaco-editor/esm/vs/language/typescript/ts.worker.js' {
  export class TypeScriptWorker {
    constructor(ctx: unknown, createData: unknown);
    getLanguageService(): {
      getEncodedSemanticClassifications(fileName: string, span: { start: number; length: number }, format: '2020'): { spans: number[] };
    };
  }
  export function initialize(create: (ctx: unknown, createData: unknown) => unknown): void;
}
declare module 'monaco-editor/esm/vs/basic-languages/typescript/typescript.js' {
  export const language: import('monaco-editor').languages.IMonarchLanguage;
}
declare module 'monaco-editor/esm/vs/basic-languages/javascript/javascript.js' {
  export const language: import('monaco-editor').languages.IMonarchLanguage;
}
