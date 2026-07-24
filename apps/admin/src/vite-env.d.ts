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
