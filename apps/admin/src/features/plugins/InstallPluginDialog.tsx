import { useMutation } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Button,
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  Textarea,
  toastApiError,
} from '@dcms/ui';
import { SchemaForm } from '../../components/SchemaForm';
import { SlugField } from '../../components/SlugField';
import { api } from '../../lib/api';
import { isSlugTaken, useAutoSlug } from '../../lib/slug';
import { usePluginUi } from '../../pluginHost/api';
import { usePluginWidgets } from '../../pluginHost/host';
import { type PluginManifest, parseConfigSchema } from './api';
import { configWidgets } from './configWidgets';

/**
 * Adding a plugin instance: its name, slug, description and configuration.
 *
 * <p>One dialog, opened from the Marketplace (where people choose a plugin) and from an old
 * `/plugins?add=<id>` link. The Plugins page used to have its own catalog tab with this dialog
 * in it, which made two shopfronts for the same plugins — and Marketplace's "Add" button
 * forwarded to that page, which then ignored what it had been asked to add.</p>
 */
export function InstallPluginDialog({
  manifest,
  onClose,
  onDone,
}: {
  manifest: PluginManifest;
  onClose: () => void;
  /** Called with the new instance, once it exists. */
  onDone: (instance: { slug: string }) => void;
}) {
  const { t } = useTranslation();
  const [name, setName] = useState(manifest.name);
  const [slug, setSlug] = useAutoSlug(name);
  const [takenSlug, setTakenSlug] = useState<string | null>(null);
  const [description, setDescription] = useState('');
  const [config, setConfig] = useState<unknown>({});
  const schema = parseConfigSchema(manifest.configJsonSchema);
  const ui = usePluginUi();
  const pluginWidgets = usePluginWidgets(ui.data?.find((p) => p.pluginId === manifest.id), null);
  const widgets = useMemo(() => ({ ...configWidgets, ...pluginWidgets }), [pluginWidgets]);
  // For multi-instance plugins the description distinguishes each instance's
  // purpose in the generated OpenAPI document, so it is required there.
  const descriptionRequired = manifest.allowMultipleInstances;

  const create = useMutation({
    mutationFn: () =>
      api.post<{ slug: string }>('/admin/plugins/instances', {
        pluginId: manifest.id,
        slug: slug.trim(),
        name: name.trim(),
        description: description.trim() || undefined,
        config: JSON.stringify(config ?? {}),
      }),
    onSuccess: (created) => {
      toast.success(t('common.saved'));
      onDone(created);
    },
    onError: (e) => (isSlugTaken(e) ? setTakenSlug(slug.trim()) : toastApiError(e, t)),
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
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label>{t('common.name')}</Label>
              <Input value={name} onChange={(e) => setName(e.target.value)} />
            </div>
            <SlugField id="instance-slug" value={slug} onChange={setSlug} taken={takenSlug} placeholder="blog" />
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
                widgets={widgets}
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
