/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the tenant content API. Empty = same-origin (published site). */
  readonly VITE_API_BASE_URL?: string;
  /** A plugin instance slug to demo on the home page. */
  readonly VITE_DEMO_SLUG?: string;
  /** A content type of that instance to list. */
  readonly VITE_DEMO_CONTENT_TYPE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
