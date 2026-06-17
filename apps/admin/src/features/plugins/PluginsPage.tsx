import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Plug, Settings2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Page, PageHeader } from '../../components/Page';
import { SchemaForm } from '../../components/SchemaForm';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Card, CardContent } from '../../components/ui/card';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '../../components/ui/dialog';
import { Input } from '../../components/ui/input';
import { Label } from '../../components/ui/label';
import { CenteredSpinner } from '../../components/ui/spinner';
import { Switch } from '../../components/ui/switch';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../../components/ui/tabs';
import { ApiError, api } from '../../lib/api';
import {
  type PluginInstance,
  type PluginManifest,
  parseConfigSchema,
  usePluginCatalog,
  usePluginInstances,
} from './api';

export function PluginsPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const catalog = usePluginCatalog();
  const instances = usePluginInstances();

  const [installing, setInstalling] = useState<PluginManifest | null>(null);
  const [editing, setEditing] = useState<PluginInstance | null>(null);

  const toggle = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      api.post(`/admin/plugins/instances/${id}/${enabled ? 'enable' : 'disable'}`),
    onSuccess: async () => qc.invalidateQueries({ queryKey: ['plugin-instances'] }),
    onError: () => toast.error(t('errors.generic')),
  });

  const manifestById = new Map((catalog.data ?? []).map((m) => [m.id, m]));

  return (
    <Page>
      <PageHeader title={t('plugins.title')} />
      <Tabs defaultValue="installed">
        <TabsList>
          <TabsTrigger value="installed">{t('plugins.installed')}</TabsTrigger>
          <TabsTrigger value="catalog">{t('plugins.catalog')}</TabsTrigger>
        </TabsList>

        <TabsContent value="installed">
          {instances.isLoading ? (
            <CenteredSpinner />
          ) : (
            <div className="space-y-2">
              {(instances.data ?? []).map((inst) => (
                <Card key={inst.id}>
                  <CardContent className="flex items-center gap-3 p-4">
                    <div className="flex h-9 w-9 items-center justify-center rounded-md bg-accent text-accent-foreground">
                      <Plug className="h-4 w-4" />
                    </div>
                    <div className="min-w-0 flex-1">
                      <p className="truncate font-medium">{inst.name}</p>
                      <p className="truncate text-xs text-muted-foreground">
                        /{inst.slug} · {inst.pluginId}
                      </p>
                    </div>
                    <Button size="icon" variant="ghost" onClick={() => setEditing(inst)}>
                      <Settings2 className="h-4 w-4" />
                    </Button>
                    <Switch
                      checked={inst.enabled}
                      onCheckedChange={(v) => toggle.mutate({ id: inst.id, enabled: v })}
                    />
                  </CardContent>
                </Card>
              ))}
              {instances.data?.length === 0 ? (
                <p className="py-8 text-center text-sm text-muted-foreground">{t('common.noResults')}</p>
              ) : null}
            </div>
          )}
        </TabsContent>

        <TabsContent value="catalog">
          {catalog.isLoading ? (
            <CenteredSpinner />
          ) : (
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
              {(catalog.data ?? []).map((m) => (
                <Card key={m.id}>
                  <CardContent className="flex h-full flex-col gap-3 p-5">
                    <div className="flex items-center gap-2">
                      <Plug className="h-4 w-4 text-primary" />
                      <span className="font-medium">{m.name}</span>
                      <Badge tone="outline" className="ml-auto">
                        v{m.version}
                      </Badge>
                    </div>
                    <p className="flex-1 text-sm text-muted-foreground">{m.description}</p>
                    <div className="flex items-center justify-between">
                      <Badge tone="secondary">
                        {m.allowMultipleInstances ? t('plugins.multiInstance') : t('plugins.singleInstance')}
                      </Badge>
                      <Button size="sm" onClick={() => setInstalling(m)}>
                        {t('plugins.install')}
                      </Button>
                    </div>
                  </CardContent>
                </Card>
              ))}
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
  const [config, setConfig] = useState<unknown>({});
  const schema = parseConfigSchema(manifest.configJsonSchema);

  const create = useMutation({
    mutationFn: () =>
      api.post('/admin/plugins/instances', {
        pluginId: manifest.id,
        slug: slug.trim(),
        name: name.trim(),
        config: JSON.stringify(config ?? {}),
      }),
    onSuccess: () => {
      toast.success(t('common.saved'));
      onDone();
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : t('errors.generic')),
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
        <div className="space-y-4">
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
          {Object.keys(schema).length > 0 ? (
            <div>
              <Label className="mb-2 block">{t('plugins.configuration')}</Label>
              <SchemaForm schema={schema} formData={config} onChange={setConfig} />
            </div>
          ) : null}
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button disabled={!slug.trim() || create.isPending} onClick={() => create.mutate()}>
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
  const [config, setConfig] = useState<unknown>({});
  const schema = manifest ? parseConfigSchema(manifest.configJsonSchema) : {};

  const save = useMutation({
    mutationFn: () =>
      api.put(`/admin/plugins/instances/${instance.id}`, {
        name: name.trim(),
        config: JSON.stringify(config ?? {}),
      }),
    onSuccess: () => {
      toast.success(t('common.saved'));
      onDone();
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : t('errors.generic')),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent wide>
        <DialogHeader>
          <DialogTitle>{instance.name}</DialogTitle>
        </DialogHeader>
        <div className="space-y-4">
          <div className="space-y-1.5">
            <Label>{t('common.name')}</Label>
            <Input value={name} onChange={(e) => setName(e.target.value)} />
          </div>
          {Object.keys(schema).length > 0 ? (
            <div>
              <Label className="mb-2 block">{t('plugins.configuration')}</Label>
              <SchemaForm schema={schema} formData={config} onChange={setConfig} />
            </div>
          ) : null}
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button disabled={save.isPending} onClick={() => save.mutate()}>
            {t('actions.save')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
