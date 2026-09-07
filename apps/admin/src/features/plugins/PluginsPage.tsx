import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Check, FileText, Plug, Search, Settings2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import type { RegistryWidgetsType, WidgetProps } from '@rjsf/utils';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CenteredSpinner,
  cn,
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  EmptyState,
  Input,
  Label,
  Page,
  PageHeader,
  Switch,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Textarea,
  toastApiError,
} from '@dcms/ui';
import { SchemaForm } from '../../components/SchemaForm';
import { MediaPicker } from '../media/MediaPicker';
import { MetaConnectionWidget } from './MetaConnectionWidget';
import { api } from '../../lib/api';
import {
  type PluginInstance,
  type PluginManifest,
  parseConfigSchema,
  parseInstanceConfig,
  usePluginCatalog,
  usePluginInstances,
} from './api';

/**
 * RJSF widget for schema fields declared with `"format": "media"` (e.g. the
 * Branding plugin's logo/favicon). Renders the shared media picker so an admin can
 * upload a new image — processed through the standard media pipeline and stored in
 * tenant media — or pick an existing asset; the field stores the media asset id.
 */
function MediaFieldWidget({ value, onChange }: WidgetProps) {
  return (
    <MediaPicker
      value={(value as string) || undefined}
      onChange={(id) => onChange(id ?? undefined)}
      category="Image"
    />
  );
}

const configWidgets: RegistryWidgetsType = {
  media: MediaFieldWidget,
  // `"format": "meta-connection"` -- the Instagram/Facebook plugins' connectionId.
  // A connection is the product of an OAuth round trip, so it cannot be typed in.
  'meta-connection': MetaConnectionWidget,
};

export function PluginsPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const catalog = usePluginCatalog();
  const instances = usePluginInstances();

  const [installing, setInstalling] = useState<PluginManifest | null>(null);
  const [editing, setEditing] = useState<PluginInstance | null>(null);
  const [query, setQuery] = useState('');

  const toggle = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      api.post(`/admin/plugins/instances/${id}/${enabled ? 'enable' : 'disable'}`),
    onSuccess: async () => qc.invalidateQueries({ queryKey: ['plugin-instances'] }),
    onError: () => toast.error(t('errors.generic')),
  });

  const manifestById = new Map((catalog.data ?? []).map((m) => [m.id, m]));
  const installedIds = new Set((instances.data ?? []).map((i) => i.pluginId));
  const allInstances = instances.data ?? [];
  const enabledCount = allInstances.filter((i) => i.enabled).length;

  const q = query.trim().toLowerCase();
  const matches = (text: string) => !q || text.toLowerCase().includes(q);
  const filteredInstances = allInstances.filter(
    (i) => matches(i.name) || matches(i.slug) || matches(i.pluginId),
  );
  const filteredCatalog = (catalog.data ?? []).filter(
    (m) => matches(m.name) || matches(m.description),
  );

  return (
    <Page>
      <PageHeader
        title={t('plugins.title')}
        description={t('plugins.subtitle')}
        actions={
          <div className="relative w-64 max-w-full">
            <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder={t('plugins.searchPlaceholder')}
              className="pl-8"
            />
          </div>
        }
      />
      <Tabs defaultValue="installed">
        <TabsList>
          <TabsTrigger value="installed">
            {t('plugins.installed')} ({allInstances.length})
          </TabsTrigger>
          <TabsTrigger value="catalog">{t('plugins.catalog')}</TabsTrigger>
        </TabsList>

        <TabsContent value="installed">
          {instances.isLoading ? (
            <CenteredSpinner />
          ) : allInstances.length === 0 ? (
            <EmptyState
              icon={Plug}
              title={t('plugins.noneInstalled')}
              description={t('plugins.noneInstalledHint')}
            />
          ) : (
            <div className="space-y-3">
              <p className="text-xs text-muted-foreground">
                {t('plugins.enabledSummary', { enabled: enabledCount, total: allInstances.length })}
              </p>
              <div className="space-y-2">
                {filteredInstances.map((inst) => {
                  const manifest = manifestById.get(inst.pluginId);
                  const typeCount = manifest?.contentTypes.length ?? 0;
                  return (
                    <Card key={inst.id} className={cn(!inst.enabled && 'opacity-60')}>
                      <CardContent className="flex items-center gap-3 p-4">
                        <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-accent text-accent-foreground">
                          <Plug className="h-4 w-4" />
                        </div>
                        <div className="min-w-0 flex-1">
                          <div className="flex items-center gap-2">
                            <p className="truncate font-medium">{inst.name}</p>
                            <Badge tone={inst.enabled ? 'success' : 'secondary'}>
                              {inst.enabled ? t('plugins.enabled') : t('plugins.disabled')}
                            </Badge>
                          </div>
                          <p className="truncate text-xs text-muted-foreground">
                            /{inst.slug} · {inst.pluginId}
                            {typeCount > 0 ? ` · ${t('plugins.typeCount', { count: typeCount })}` : ''}
                          </p>
                          {inst.description ? (
                            <p className="truncate text-xs text-muted-foreground">{inst.description}</p>
                          ) : null}
                        </div>
                        <Button
                          size="icon"
                          variant="ghost"
                          title={t('plugins.configuration')}
                          onClick={() => setEditing(inst)}
                        >
                          <Settings2 className="h-4 w-4" />
                        </Button>
                        <Switch
                          checked={inst.enabled}
                          onCheckedChange={(v) => toggle.mutate({ id: inst.id, enabled: v })}
                        />
                      </CardContent>
                    </Card>
                  );
                })}
                {filteredInstances.length === 0 ? (
                  <p className="py-8 text-center text-sm text-muted-foreground">{t('common.noResults')}</p>
                ) : null}
              </div>
            </div>
          )}
        </TabsContent>

        <TabsContent value="catalog">
          {catalog.isLoading ? (
            <CenteredSpinner />
          ) : (
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
              {filteredCatalog.map((m) => {
                const isInstalled = installedIds.has(m.id);
                // A single-instance plugin can only be installed once.
                const installDisabled = isInstalled && !m.allowMultipleInstances;
                return (
                  <Card key={m.id}>
                    <CardContent className="flex h-full flex-col gap-3 p-5">
                      <div className="flex items-center gap-2">
                        <Plug className="h-4 w-4 text-primary" />
                        <span className="font-medium">{m.name}</span>
                        {isInstalled ? (
                          <Badge tone="success" className="gap-1">
                            <Check className="h-3 w-3" />
                            {t('plugins.installedBadge')}
                          </Badge>
                        ) : null}
                        <Badge tone="outline" className="ml-auto">
                          v{m.version}
                        </Badge>
                      </div>
                      <p className="flex-1 text-sm text-muted-foreground">{m.description}</p>
                      <div className="flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
                        <Badge tone="secondary">
                          {m.allowMultipleInstances
                            ? t('plugins.multiInstance')
                            : t('plugins.singleInstance')}
                        </Badge>
                        {m.contentTypes.length > 0 ? (
                          <span className="inline-flex items-center gap-1">
                            <FileText className="h-3.5 w-3.5" />
                            {t('plugins.typeCount', { count: m.contentTypes.length })}
                          </span>
                        ) : null}
                      </div>
                      <div className="flex items-center justify-end">
                        <Button
                          size="sm"
                          variant={installDisabled ? 'outline' : 'default'}
                          disabled={installDisabled}
                          onClick={() => setInstalling(m)}
                        >
                          {installDisabled ? t('plugins.installedBadge') : t('plugins.install')}
                        </Button>
                      </div>
                    </CardContent>
                  </Card>
                );
              })}
              {filteredCatalog.length === 0 ? (
                <p className="col-span-full py-8 text-center text-sm text-muted-foreground">
                  {t('common.noResults')}
                </p>
              ) : null}
            </div>
          )}
        </TabsContent>
      </Tabs>

      {installing ? (
        <InstallDialog
          manifest={installing}
          onClose={() => setInstalling(null)}
          onDone={async () => {
            setInstalling(null);
            await qc.invalidateQueries({ queryKey: ['plugin-instances'] });
          }}
        />
      ) : null}

      {editing ? (
        <ConfigDialog
          instance={editing}
          manifest={manifestById.get(editing.pluginId)}
          onClose={() => setEditing(null)}
          onDone={async () => {
            setEditing(null);
            await qc.invalidateQueries({ queryKey: ['plugin-instances'] });
          }}
        />
      ) : null}
    </Page>
  );
}

function InstallDialog({
  manifest,
  onClose,
  onDone,
}: {
  manifest: PluginManifest;
  onClose: () => void;
  onDone: () => void;
}) {
  const { t } = useTranslation();
  const [slug, setSlug] = useState('');
  const [name, setName] = useState(manifest.name);
  const [description, setDescription] = useState('');
  const [config, setConfig] = useState<unknown>({});
  const schema = parseConfigSchema(manifest.configJsonSchema);
  // For multi-instance plugins the description distinguishes each instance's
  // purpose in the generated OpenAPI document, so it is required there.
  const descriptionRequired = manifest.allowMultipleInstances;

  const create = useMutation({
    mutationFn: () =>
      api.post('/admin/plugins/instances', {
        pluginId: manifest.id,
        slug: slug.trim(),
        name: name.trim(),
        description: description.trim() || undefined,
        config: JSON.stringify(config ?? {}),
      }),
    onSuccess: () => {
      toast.success(t('common.saved'));
      onDone();
    },
    onError: (e) => toastApiError(e, t),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent wide>
        <DialogHeader>
          <DialogTitle>
            {t('plugins.install')}: {manifest.name}
          </DialogTitle>
          <DialogDescription>{manifest.description}</DialogDescription>
        </DialogHeader>
        <DialogBody className="space-y-4">
          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label>{t('common.slug')}</Label>
              <Input value={slug} onChange={(e) => setSlug(e.target.value)} placeholder="blog" />
            </div>
            <div className="space-y-1.5">
              <Label>{t('common.name')}</Label>
              <Input value={name} onChange={(e) => setName(e.target.value)} />
            </div>
          </div>
          <div className="space-y-1.5">
            <Label>
              {t('plugins.description')}
              {descriptionRequired ? <span className="ml-1 text-destructive">*</span> : null}
            </Label>
            <Textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder={t('plugins.descriptionPlaceholder')}
            />
            <p className="text-xs text-muted-foreground">{t('plugins.descriptionHint')}</p>
          </div>
          {Object.keys(schema).length > 0 ? (
            <div>
              <Label className="mb-2 block">{t('plugins.configuration')}</Label>
              <SchemaForm
                schema={schema}
                formData={config}
                onChange={setConfig}
                widgets={configWidgets}
              />
            </div>
          ) : null}
        </DialogBody>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button
            disabled={
              !slug.trim() || (descriptionRequired && !description.trim()) || create.isPending
            }
            onClick={() => create.mutate()}
          >
            {t('plugins.install')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function ConfigDialog({
  instance,
  manifest,
  onClose,
  onDone,
}: {
  instance: PluginInstance;
  manifest?: PluginManifest;
  onClose: () => void;
  onDone: () => void;
}) {
  const { t } = useTranslation();
  const [name, setName] = useState(instance.name);
  const [description, setDescription] = useState(instance.description ?? '');
  // Seeded from what is stored: the form posts the whole config back, so starting
  // empty would wipe it on save.
  const [config, setConfig] = useState<unknown>(() => parseInstanceConfig(instance.config));
  const schema = manifest ? parseConfigSchema(manifest.configJsonSchema) : {};
  const descriptionRequired = manifest?.allowMultipleInstances ?? false;

  const save = useMutation({
    mutationFn: () =>
      api.put(`/admin/plugins/instances/${instance.id}`, {
        name: name.trim(),
        description: description.trim() || undefined,
        config: JSON.stringify(config ?? {}),
      }),
    onSuccess: () => {
      toast.success(t('common.saved'));
      onDone();
    },
    onError: (e) => toastApiError(e, t),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent wide>
        <DialogHeader>
          <DialogTitle>{instance.name}</DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-4">
          <div className="space-y-1.5">
            <Label>{t('common.name')}</Label>
            <Input value={name} onChange={(e) => setName(e.target.value)} />
          </div>
          <div className="space-y-1.5">
            <Label>
              {t('plugins.description')}
              {descriptionRequired ? <span className="ml-1 text-destructive">*</span> : null}
            </Label>
            <Textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder={t('plugins.descriptionPlaceholder')}
            />
            <p className="text-xs text-muted-foreground">{t('plugins.descriptionHint')}</p>
          </div>
          {Object.keys(schema).length > 0 ? (
            <div>
              <Label className="mb-2 block">{t('plugins.configuration')}</Label>
              <SchemaForm
                schema={schema}
                formData={config}
                onChange={setConfig}
                widgets={configWidgets}
                // The Meta widget's Sync-now button needs the instance it belongs to,
                // which is a fact about this dialog rather than about the field.
                formContext={{ instanceId: instance.id }}
              />
            </div>
          ) : null}
        </DialogBody>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button
            disabled={(descriptionRequired && !description.trim()) || save.isPending}
            onClick={() => save.mutate()}
          >
            {t('actions.save')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
