import { useQuery } from '@tanstack/react-query';
import { pluginNamespace, type PluginAdminModule } from '@dcms/plugin-ui';
import i18n from '../lib/i18n';
import type { PluginModuleRef } from './api';

/**
 * Built-in plugins' admin UIs, compiled into the console from beside each plugin's C#:
 * `src/Plugins/{Assembly}/admin/src/index.tsx`. Keyed by the assembly folder, which is what the
 * server names (`module.key`), so the console lists no plugin by name — adding a plugin with a
 * UI is adding the folder. Lazy: a plugin's code is fetched the first time one of its screens
 * or widgets is needed.
 */
const builtin = import.meta.glob<{ default: PluginAdminModule }>('../../../../src/Plugins/*/admin/src/index.tsx');

const builtinByKey = new Map(
  Object.entries(builtin).map(([path, load]) => [path.split('/src/Plugins/')[1].split('/')[0], load]),
);

/** Whether the console can render this module at all (a built-in plugin without a UI folder cannot). */
export function hasModule(ref: PluginModuleRef | null | undefined): boolean {
  if (!ref) return false;
  return ref.kind === 'url' || builtinByKey.has(ref.key);
}

const loaded = new Map<string, Promise<PluginAdminModule>>();

/**
 * Loads a plugin's admin module once per page. An installed plugin's comes from its folder
 * (`import()` of the URL the server gave, plus its stylesheet); either way its translations are
 * registered under the plugin's namespace before any of its components render.
 */
export function loadPluginModule(pluginId: string, ref: PluginModuleRef): Promise<PluginAdminModule> {
  const cacheKey = ref.kind === 'builtin' ? `builtin:${ref.key}` : ref.url;
  let promise = loaded.get(cacheKey);
  if (!promise) {
    promise = importModule(ref).then((module) => {
      for (const [language, bundle] of Object.entries(module.locales ?? {})) {
        i18n.addResourceBundle(language, pluginNamespace(pluginId), bundle, true, true);
      }
      return module;
    });
    // A failed load may be a network blip; let the next attempt try again.
    promise.catch(() => loaded.delete(cacheKey));
    loaded.set(cacheKey, promise);
  }
  return promise;
}

async function importModule(ref: PluginModuleRef): Promise<PluginAdminModule> {
  if (ref.kind === 'builtin') {
    const load = builtinByKey.get(ref.key);
    if (!load) throw new Error(`This console has no admin UI for ${ref.key}.`);
    return (await load()).default;
  }
  if (ref.css && !document.querySelector(`link[data-plugin-css="${CSS.escape(ref.css)}"]`)) {
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = ref.css;
    link.dataset.pluginCss = ref.css;
    document.head.appendChild(link);
  }
  const module = (await import(/* @vite-ignore */ ref.url)) as { default?: PluginAdminModule };
  if (!module.default) throw new Error('The plugin UI module has no default export (definePluginAdmin).');
  return module.default;
}

/** React-friendly load: cached per page, retried once, shared by every screen and widget of the plugin. */
export function usePluginModule(pluginId: string | undefined, ref: PluginModuleRef | null | undefined) {
  return useQuery({
    queryKey: ['plugin-module', pluginId, ref?.kind === 'builtin' ? ref.key : ref?.url],
    enabled: !!pluginId && hasModule(ref),
    staleTime: Infinity,
    gcTime: Infinity,
    queryFn: () => loadPluginModule(pluginId!, ref!),
  });
}
