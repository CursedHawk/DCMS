import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  CenteredSpinner,
  Input,
  Label,
  Switch,
  Textarea,
  toastApiError,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { SchemaForm } from '../../components/SchemaForm';
import { usePluginUi } from '../../pluginHost/api';
import { usePluginWidgets } from '../../pluginHost/host';
import { configWidgets } from '../plugins/configWidgets';
import {
  parseConfigSchema,
  parseInstanceConfig,
  usePluginCatalog,
  usePluginInstances,
  type PluginInstance,
  type PluginManifest,
} from '../plugins/api';

/**
 * An instance's name, description, AI tools switch and the form its config schema describes.
 *
 * <p>Every plugin provides its app's Settings screen (#337); this is what most of them provide —
 * `SchemaSettings` from `@dcms/plugin-ui` renders it through `host.components.SchemaSettings` — and
 * what a plugin's own settings screen may embed beside its own controls.</p>
 */
export function SchemaSettings({ slug }: { slug: string }) {
  const instances = usePluginInstances();
  const catalog = usePluginCatalog();
  const instance = instances.data?.find((i) => i.slug === slug);
  const manifest = catalog.data?.find((m) => m.id === instance?.pluginId);
  if (!instance || !manifest) return instances.isLoading || catalog.isLoading ? <CenteredSpinner /> : null;
  return <SettingsForm key={instance.id} instance={instance} manifest={manifest} />;
}

function SettingsForm({ instance, manifest }: { instance: PluginInstance; manifest: PluginManifest }) {
  const ui = usePluginUi();
  // The plugin's own widgets for custom formats in its config (the Meta account picker).
  const pluginWidgets = usePluginWidgets(
    ui.data?.find((p) => p.pluginId === manifest.id),
    useMemo(
      () => ({ id: instance.id, slug: instance.slug, name: instance.name, enabled: instance.enabled }),
      [instance.id, instance.slug, instance.name, instance.enabled],
    ),
  );
  const widgets = useMemo(() => ({ ...configWidgets, ...pluginWidgets }), [pluginWidgets]);
  const { t } = useTranslation();
  const qc = useQueryClient();
  const [name, setName] = useState(instance.name);
  const [description, setDescription] = useState(instance.description ?? '');
  const [aiTools, setAiTools] = useState(instance.aiToolsEnabled ?? false);
  // Seeded from what is stored: the form posts the whole config back, so starting empty would wipe it.
  const [config, setConfig] = useState<unknown>(() => parseInstanceConfig(instance.config));
  useEffect(() => setConfig(parseInstanceConfig(instance.config)), [instance.config]);
  const schema = parseConfigSchema(manifest.configJsonSchema);
  const descriptionRequired = manifest.allowMultipleInstances;
  // Only a plugin that offers contracts has anything an AI agent could use.
  const offersContracts = (manifest.provides?.length ?? 0) > 0;

  const save = useMutation({
    mutationFn: () =>
      api.put(`/admin/plugins/instances/${instance.id}`, {
        name: name.trim(),
        description: description.trim() || undefined,
        config: JSON.stringify(config ?? {}),
        aiToolsEnabled: aiTools,
      }),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await qc.invalidateQueries({ queryKey: ['plugin-instances'] });
      // The AI tools switch decides what the assistant may use, and plugin screens show it.
      await qc.invalidateQueries({ queryKey: ['contracts', 'ai'] });
      await qc.invalidateQueries({ queryKey: ['plugin-ui'] });
      // The app's name is its menu entry.
      await qc.invalidateQueries({ queryKey: ['navigation'] });
      // Data sets may describe themselves from config (VisitorAuth's attribute columns).
      await qc.invalidateQueries({ queryKey: ['plugin-data'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  return (
    <div className="max-w-3xl space-y-5">
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <div className="space-y-1.5">
          <Label htmlFor="plugin-name">{t('common.name')}</Label>
          <Input id="plugin-name" value={name} onChange={(e) => setName(e.target.value)} />
        </div>
        <div className="space-y-1.5">
          <Label>{t('common.slug')}</Label>
          <Input value={instance.slug} disabled className="font-mono" />
        </div>
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="plugin-description">
          {t('plugins.description')}
          {descriptionRequired ? <span className="ml-1 text-destructive">*</span> : null}
        </Label>
        <Textarea
          id="plugin-description"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          placeholder={t('plugins.descriptionPlaceholder')}
        />
        <p className="text-xs text-muted-foreground">{t('plugins.descriptionHint')}</p>
      </div>
      {offersContracts ? (
        <div className="flex items-start justify-between gap-4 rounded-md border p-3">
          <div className="space-y-0.5">
            <Label htmlFor="plugin-ai-tools">{t('plugins.aiTools.label')}</Label>
            <p className="text-xs text-muted-foreground">{t('plugins.aiTools.hint')}</p>
          </div>
          <Switch id="plugin-ai-tools" checked={aiTools} onCheckedChange={setAiTools} />
        </div>
      ) : null}
      {Object.keys(schema).length > 0 ? (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">{t('plugins.configuration')}</CardTitle>
          </CardHeader>
          <CardContent>
            <SchemaForm
              schema={schema}
              formData={config}
              onChange={setConfig}
              widgets={widgets}
              // Widgets' Sync-now buttons need the instance they belong to (Meta by id,
              // plugin-owned routes by slug).
              formContext={{ instanceId: instance.id, instanceSlug: instance.slug }}
            />
          </CardContent>
        </Card>
      ) : null}
      <div className="flex justify-end">
        <Button disabled={(descriptionRequired && !description.trim()) || save.isPending} onClick={() => save.mutate()}>
          {t('actions.save')}
        </Button>
      </div>
    </div>
  );
}
