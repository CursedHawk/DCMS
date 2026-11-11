import { useTranslation } from 'react-i18next';
import { PackageX } from 'lucide-react';
import { CenteredSpinner, EmptyState, RequirePermission } from '@dcms/ui';
import { resolvePermission, type PluginInstanceRef } from '@dcms/plugin-ui';
import { can, useMyPermissions } from '../lib/permissions';
import { screenTitle, type PluginUiInfo } from './api';
import { PluginErrorBoundary, PluginFrame } from './host';
import { hasModule, usePluginModule } from './modules';

/**
 * One plugin screen, whatever plugin it belongs to: guarded by the permission the manifest
 * names (on the screen's instance: plugin permissions are held per instance), loaded from the plugin's module, rendered inside the plugin's frame and its own error
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
  const me = useMyPermissions(instance === null);

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

  // Plugin permissions are held per instance: this screen's instance, or (outside an app) any.
  const perm = instance
    ? resolvePermission(plugin.pluginId, screen.permission, instance.slug)
    : (plugin.instances
        .map((i) => resolvePermission(plugin.pluginId, screen.permission, i.slug))
        .find((key) => can(me.data, key)) ?? screen.permission);

  return (
    <RequirePermission perm={perm}>
      <PluginErrorBoundary pluginName={plugin.name}>
        <PluginFrame screen={{ pluginId: plugin.pluginId, screenId, instance, instances: plugin.instances }}>
          <Screen pluginId={plugin.pluginId} screenId={screenId} instance={instance} instances={plugin.instances} />
        </PluginFrame>
      </PluginErrorBoundary>
    </RequirePermission>
  );
}
