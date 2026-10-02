/**
 * Where a Mode D site keeps its documents. Everything the builder owns lives under `dcms/`;
 * the rest of the repo is an ordinary Vite application (see ADR 0020).
 */

export const APP_JSON = 'dcms/app.json';
export const THEME_JSON = 'dcms/theme.json';

const PAGE_FILE = /^dcms\/pages\/([a-z0-9][a-z0-9-]*)\.json$/;

export function pagePath(id: string): string {
  return `dcms/pages/${id}.json`;
}

export function pageIdFromPath(path: string): string | null {
  return PAGE_FILE.exec(path)?.[1] ?? null;
}
