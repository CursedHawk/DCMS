import * as React from 'react';
import * as jsxRuntime from 'react/jsx-runtime';
import * as ReactDOM from 'react-dom';
import * as ReactI18next from 'react-i18next';
import * as ReactQuery from '@tanstack/react-query';
import * as core from '@dcms/core';
import * as ui from '@dcms/ui';
import * as pluginUi from '@dcms/plugin-ui';
import { SHARED_GLOBAL, type SharedModuleId } from '@dcms/plugin-ui/shared';

/**
 * Publishes the console's copies of the shared modules for installed plugins' bundles, which
 * import them through the `@dcms/plugin-ui/vite` preset instead of bundling their own. Typed
 * against the SDK's list, so a module added there and forgotten here fails the build.
 */
export function exposeSharedModules(): void {
  const modules: Record<SharedModuleId, unknown> = {
    react: React,
    'react/jsx-runtime': jsxRuntime,
    'react-dom': ReactDOM,
    'react-i18next': ReactI18next,
    '@tanstack/react-query': ReactQuery,
    '@dcms/core': core,
    '@dcms/ui': ui,
    '@dcms/plugin-ui': pluginUi,
  };
  (globalThis as Record<string, unknown>)[SHARED_GLOBAL] = modules;
}
