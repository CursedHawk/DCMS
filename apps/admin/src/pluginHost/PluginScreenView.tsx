import { useTranslation } from 'react-i18next';
import { PackageX } from 'lucide-react';
import { CenteredSpinner, EmptyState, RequirePermission } from '@dcms/ui';
import type { PluginInstanceRef } from '@dcms/plugin-ui';
import { screenTitle, type PluginUiInfo } from './api';
import { PluginErrorBoundary, PluginFrame } from './host';
import { hasModule, usePluginModule } from './modules';

/**
 * One plugin screen, whatever plugin it belongs to: guarded by the permission the manifest
 * names, loaded from the plugin's module, rendered inside the plugin's frame and its own error
 * boundary. Every way it can fail to appear says why, in terms of what the reader can do.
 */
export function PluginScreenView({
  plugin,
  screenId,
  instance,
}: {
  plugin: PluginUiInfo;
  screenId: string;
  instance: PluginInstanceRef | null;
}) {
  const { t, i18n } = useTranslation();
  const screen = plugin.screens.find((s) => s.id === screenId);
  const module = usePluginModule(plugin.pluginId, plugin.module);

  if (!screen) {
    return <EmptyState icon={PackageX} title={t('pluginHost.noScreen', { plugin: plugin.name })} />;
  }
  const title = screenTitle(screen, i18n.language);
  if (!hasModule(plugin.module)) {
    return (
      <EmptyState
        icon={PackageX}
        title={t('pluginHost.unavailable', { screen: title })}
        description={t('pluginHost.unavailableHint', { plugin: plugin.name })}
      />
    );
  }
  if (module.isLoading) return <CenteredSpinner />;
  const Screen = module.data?.screens?.[screenId];
  if (module.isError || !Screen) {
    return (
      <EmptyState
        icon={PackageX}
        title={t('pluginHost.unavailable', { screen: title })}
        description={
          module.error instanceof Error
            ? t('pluginHost.loadFailed', { plugin: plugin.name, message: module.error.message })
            : t('pluginHost.unavailableHint', { plugin: plugin.name })
        }
      />
    );
  }

  return (
    <RequirePermission perm={screen.permission}>
      <PluginErrorBoundary pluginName={plugin.name}>
        <PluginFrame screen={{ pluginId: plugin.pluginId, screenId, instance, instances: plugin.instances }}>
          <Screen pluginId={plugin.pluginId} screenId={screenId} instance={instance} instances={plugin.instances} />
        </PluginFrame>
      </PluginErrorBoundary>
    </RequirePermission>
  );
}
