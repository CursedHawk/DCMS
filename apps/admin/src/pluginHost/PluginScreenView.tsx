import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { PackageX, PowerOff } from 'lucide-react';
import { CenteredSpinner, EmptyState, Page, PageHeader, RequirePermission } from '@dcms/ui';
import type { PluginInstanceRef } from '@dcms/plugin-ui';
import { iconByName } from '../app/navApi';
import { screenTitle, usePluginUi, type PluginUiInfo } from './api';
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

/**
 * `/app/{pluginId}/{screenId}`: a plugin-wide screen as a page of its own. The plugin draws the
 * whole page; the console only decides whether it may be drawn. A plugin with no enabled
 * instance is switched off, and says so rather than showing a screen that has nothing behind it.
 */
export function PluginAppPage({ pluginId, screenId }: { pluginId: string; screenId: string }) {
  const { t } = useTranslation();
  const ui = usePluginUi();
  const plugin = ui.data?.find((p) => p.pluginId === pluginId);

  if (ui.isLoading) return <CenteredSpinner />;
  if (!plugin) {
    return (
      <Page>
        <EmptyState icon={PackageX} title={t('pluginHost.noPlugin')} description={t('pluginHost.noPluginHint', { plugin: pluginId })} />
      </Page>
    );
  }
  if (!plugin.instances.some((i) => i.enabled)) {
    const first = plugin.instances[0];
    const Icon = iconByName(plugin.icon);
    return (
      <Page>
        <EmptyState
          icon={first ? PowerOff : Icon}
          title={first ? t('pluginHost.off', { plugin: plugin.name }) : t('pluginHost.notInstalled', { plugin: plugin.name })}
          description={first ? t('pluginHost.offHint') : t('pluginHost.notInstalledHint')}
          action={
            <Link
              to={(first ? '/plugins/$slug' : '/marketplace') as string}
              params={(first ? { slug: first.slug } : {}) as never}
              className="text-sm font-medium text-primary hover:underline"
            >
              {first ? t('pluginHost.openPlugin', { plugin: plugin.name }) : t('pluginHost.openMarketplace')}
            </Link>
          }
        />
      </Page>
    );
  }
  return <PluginScreenView plugin={plugin} screenId={screenId} instance={null} />;
}

/**
 * `/plugins/{slug}/{screenId}` for a member who does not manage plugins: the screen alone.
 *
 * <p>An app's records or a realm's users are daily work for people who may never configure the
 * plugin — and the instance page around these screens (settings, wiring, data sets, history) is
 * plugin management, gated on plugins:manage. So a menu entry for "Records (CRM)" used to lead
 * those people to a refusal. Here they get the screen under a plain header; the screen's own
 * permission still guards it (PluginScreenView), and the server guards every call it makes.</p>
 *
 * <p>Everything comes from `/admin/plugin-ui`, which any member may read — not from the plugin
 * management endpoints the full instance page uses.</p>
 */
export function PluginInstanceScreenPage({ slug, screenId }: { slug: string; screenId: string }) {
  const { t, i18n } = useTranslation();
  const ui = usePluginUi();

  if (ui.isLoading) return <CenteredSpinner />;
  const plugin = ui.data?.find((p) => p.instances.some((i) => i.slug === slug));
  const instance = plugin?.instances.find((i) => i.slug === slug);
  if (!plugin || !instance) {
    return (
      <Page>
        <EmptyState icon={PackageX} title={t('pluginPage.notFound')} description={t('pluginPage.notFoundHint', { slug })} />
      </Page>
    );
  }
  if (!instance.enabled) {
    return (
      <Page>
        <EmptyState icon={PowerOff} title={t('pluginHost.off', { plugin: instance.name })} description={t('pluginHost.offHint')} />
      </Page>
    );
  }
  const screen = plugin.screens.find((s) => s.id === screenId);
  return (
    <Page className="max-w-7xl">
      <PageHeader title={screen ? screenTitle(screen, i18n.language) : screenId} description={instance.name} />
      <PluginScreenView plugin={plugin} screenId={screenId} instance={instance} />
    </Page>
  );
}
