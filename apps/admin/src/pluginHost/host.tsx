import { Component, useMemo, type ComponentType, type ErrorInfo, type ReactNode } from 'react';
import { useNavigate } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { RegistryWidgetsType, WidgetProps } from '@rjsf/utils';
import { AlertTriangle } from 'lucide-react';
import { Button, CenteredSpinner, EmptyState } from '@dcms/ui';
import {
  PluginHostProvider,
  PluginScreenProvider,
  type PluginHost,
  type PluginScreenProps,
  type PluginWidgetProps,
} from '@dcms/plugin-ui';
import { api } from '../lib/api';
import { can, useMyPermissions } from '../lib/permissions';
import { runtimeConfig } from '../runtime-config';
import { adminHeaders, getCurrentTenantSlug } from '../tenants';
import { DataSetView } from '../features/plugins/DataSetView';
import { useDataSets } from '../features/plugins/dataApi';
import type { PluginUiInfo } from './api';
import { usePluginModule } from './modules';
import { useOptionalAi } from '../features/assistant/context';

/** A data set embedded in a plugin screen: the plugin page's own view, by slug and set id. */
function EmbeddedDataSet({ slug, set }: { slug: string; set: string }) {
  const sets = useDataSets(slug);
  const found = sets.data?.find((s) => s.id === set);
  if (sets.isLoading) return <CenteredSpinner />;
  return found ? <DataSetView slug={slug} set={found} /> : null;
}

/** The console's services for plugin code, built once per render of a plugin frame. */
export function useConsolePluginHost(): PluginHost {
  const navigate = useNavigate();
  const me = useMyPermissions(true);
  const ai = useOptionalAi();
  const setPage = ai?.setPage;
  const setOpen = ai?.setOpen;
  return useMemo<PluginHost>(
    () => ({
      api,
      tenant: getCurrentTenantSlug(),
      authHeaders: adminHeaders,
      contentApiBase: runtimeConfig.contentApiBase,
      can: (permission) => can(me.data, permission),
      navigate: (to) => void navigate({ to: to as string }),
      assistant: setPage && setOpen ? { setContext: setPage, open: () => setOpen(true) } : undefined,
      components: { DataSet: EmbeddedDataSet },
    }),
    [me.data, navigate, setPage, setOpen],
  );
}

/** Everything a plugin component expects around it: the host and which screen (or widget) it is. */
export function PluginFrame({ screen, children }: { screen: PluginScreenProps; children: ReactNode }) {
  const host = useConsolePluginHost();
  return (
    <PluginHostProvider host={host}>
      <PluginScreenProvider screen={screen}>{children}</PluginScreenProvider>
    </PluginHostProvider>
  );
}

/**
 * Keeps a plugin's failure inside the plugin's area: the menu, the header and every other
 * screen stay usable, and the reader is told whose code failed and what to try.
 */
export class PluginErrorBoundary extends Component<
  { pluginName: string; children: ReactNode },
  { error: Error | null }
> {
  state = { error: null as Error | null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error(`Plugin UI (${this.props.pluginName}) failed`, error, info.componentStack);
  }

  render() {
    if (!this.state.error) return this.props.children;
    return <PluginCrashed pluginName={this.props.pluginName} message={this.state.error.message} />;
  }
}

function PluginCrashed({ pluginName, message }: { pluginName: string; message: string }) {
  const { t } = useTranslation();
  return (
    <EmptyState
      icon={AlertTriangle}
      title={t('pluginHost.crashed', { plugin: pluginName })}
      description={t('pluginHost.crashedHint', { message })}
      action={
        <Button variant="outline" onClick={() => window.location.reload()}>
          {t('pluginHost.reload')}
        </Button>
      }
    />
  );
}

/**
 * The RJSF widgets a plugin contributes, each wrapped in that plugin's frame so its hooks work
 * inside a config form. `instance` is the instance whose config the form edits (none while
 * installing).
 */
export function usePluginWidgets(
  plugin: PluginUiInfo | undefined,
  instance: PluginScreenProps['instance'],
): RegistryWidgetsType {
  const module = usePluginModule(plugin?.pluginId, plugin?.module);
  return useMemo(() => {
    const widgets = module.data?.configWidgets ?? {};
    if (!plugin) return {};
    const screen: PluginScreenProps = {
      pluginId: plugin.pluginId,
      screenId: 'config',
      instance,
      instances: plugin.instances,
    };
    return Object.fromEntries(
      Object.entries(widgets).map(([format, Widget]) => {
        const Inner = Widget as ComponentType<PluginWidgetProps>;
        const Wrapped = (props: WidgetProps) => (
          <PluginErrorBoundary pluginName={plugin.name}>
            <PluginFrame screen={screen}>
              <Inner {...(props as unknown as PluginWidgetProps)} />
            </PluginFrame>
          </PluginErrorBoundary>
        );
        Wrapped.displayName = `PluginWidget(${plugin.pluginId}:${format})`;
        return [format, Wrapped];
      }),
    );
  }, [module.data, plugin, instance]);
}
