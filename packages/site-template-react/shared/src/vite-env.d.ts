/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the tenant content API. Empty = same origin, which is right for the published site. */
  readonly VITE_API_BASE_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
