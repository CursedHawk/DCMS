/**
 * Modules a plugin's admin UI shares with the console instead of bundling its own copy.
 *
 * One React, one design system, one query cache, one i18n: a plugin bundling a second React
 * breaks hooks outright, and a second @dcms/ui or react-query would render outside the
 * console's theme and cache. Built-in plugins share them by being compiled with the console;
 * an installed plugin's bundle reaches them through `globalThis.__DCMS_SHARED__`, which the
 * console fills before it loads any plugin and the Vite preset (`@dcms/plugin-ui/vite`)
 * points these imports at.
 *
 * Adding an id here is a compatibility promise to every installed plugin; removing one breaks
 * them. Treat it like the SDK's public surface.
 */
export const SHARED_MODULES = [
  'react',
  'react/jsx-runtime',
  'react-dom',
  'react-i18next',
  '@tanstack/react-query',
  '@dcms/core',
  '@dcms/ui',
  '@dcms/plugin-ui',
] as const;

export type SharedModuleId = (typeof SHARED_MODULES)[number];

/** The global the console publishes shared modules on. */
export const SHARED_GLOBAL = '__DCMS_SHARED__';
