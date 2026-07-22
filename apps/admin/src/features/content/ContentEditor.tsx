import { useMutation, useQueryClient } from '@tanstack/react-query';
import { CalendarClock, History } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '../../components/ui/dialog';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Label } from '../../components/ui/label';
import { CenteredSpinner } from '../../components/ui/spinner';
import { ApiError, api } from '../../lib/api';
import {
  type ContentFieldDef,
  type ContentTypeDef,
  type CustomFieldDef,
  customFieldsOf,
  parseInstanceConfig,
  usePluginInstances,
} from '../plugins/api';
import { ContentFieldInput } from './ContentFieldInput';
import { useContentItem } from './api';

/**
 * Present a tenant-defined field as a plugin field, so admin-defined and built-in
 * fields go through one renderer instead of two that drift apart.
 */
function asFieldDef(custom: CustomFieldDef): ContentFieldDef {
  const type = (
    {
      text: 'Text',
      longText: 'RichText',
      number: 'Number',
      boolean: 'Boolean',
      date: 'DateTime',
      tags: 'Tags',
      url: 'Text',
      image: 'MediaRef',
    } as const
  )[custom.type];

  return {
    name: custom.label,
    type: type ?? 'Text',
    required: custom.required ?? false,
    description: custom.description,
    reference: custom.type === 'image' ? { mediaCategory: 'Image' } : undefined,
  };
}

export function ContentEditor({
  instanceId,
  contentType,
  itemId,
  onClose,
  onSaved,
}: {
  instanceId: string;
  contentType: ContentTypeDef;
  itemId: string | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const isNew = itemId === null;
  const detail = useContentItem(itemId ?? undefined);

  const [slug, setSlug] = useState('');
  const [data, setData] = useState<Record<string, unknown>>({});
  const [showVersions, setShowVersions] = useState(false);
  const [scheduleAt, setScheduleAt] = useState('');

  useEffect(() => {
    if (detail.data) {
      setSlug(detail.data.slug);
      setData(detail.data.draft ?? {});
    }
  }, [detail.data]);

  const setField = (name: string, v: unknown) => setData((d) => ({ ...d, [name]: v }));

  // The instance config both declares this type's tenant-defined fields and,
  // for a gig, names the Roster instance its line-up is picked from.
  const instances = usePluginInstances();
  const instance = instances.data?.find((i) => i.id === instanceId);
  const instanceConfig = parseInstanceConfig(instance?.config);

  const valuesField = contentType.customFields?.valuesField;
  const customFields = customFieldsOf(contentType, instanceConfig);
  const customValues = (valuesField ? data[valuesField] : null) as Record<string, unknown> | null;
  const setCustom = (key: string, v: unknown) =>
    setData((d) => ({
      ...d,
      [valuesField!]: { ...((d[valuesField!] as Record<string, unknown>) ?? {}), [key]: v },
    }));

  const invalidate = async () => {
    await qc.invalidateQueries({ queryKey: ['content', instanceId] });
    if (itemId) await qc.invalidateQueries({ queryKey: ['content-item', itemId] });
  };

  const save = useMutation({
    mutationFn: () =>
      isNew
        ? api.post('/admin/content', {
            pluginInstanceId: instanceId,
            contentType: contentType.name,
            slug: slug.trim(),
            data,
          })
        : api.put(`/admin/content/${itemId}`, { data }),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await invalidate();
      onSaved();
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : t('errors.generic')),
  });

  const action = useMutation({
    mutationFn: (verb: 'publish' | 'unpublish') => api.post(`/admin/content/${itemId}/${verb}`),
    onSuccess: async (_d, verb) => {
      toast.success(verb === 'publish' ? t('content.published') : t('content.draft'));
      await invalidate();
    },
    onError: () => toast.error(t('errors.generic')),
  });

  const schedule = useMutation({
    mutationFn: () =>
      api.post(`/admin/content/${itemId}/schedule`, { publishAt: new Date(scheduleAt).toISOString() }),
    onSuccess: async () => {
      toast.success(t('actions.schedule'));
      setScheduleAt('');
      await invalidate();
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : t('errors.generic')),
  });

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent wide>
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            {isNew ? t('content.newItem') : slug}
            {detail.data ? (
              <Badge tone={detail.data.status === 'Published' ? 'success' : 'secondary'}>
                {detail.data.status}
              </Badge>
            ) : null}
          </DialogTitle>
        </DialogHeader>

        {!isNew && detail.isLoading ? (
          <CenteredSpinner />
        ) : (
          <DialogBody className="space-y-4">
            <div className="space-y-1.5">
              <Label>{t('common.slug')}</Label>
              <Input
                value={slug}
                onChange={(e) => setSlug(e.target.value)}
                disabled={!isNew}
                placeholder="my-first-post"
              />
            </div>

            {contentType.fields
              // The custom values are edited field by field below, never as raw JSON.
              .filter((f) => f.name !== valuesField)
              .map((f) => (
                <ContentFieldInput
                  key={f.name}
                  field={f}
                  instanceConfig={instanceConfig}
                  value={data[f.name]}
                  onChange={(v) => setField(f.name, v)}
                />
              ))}

            {valuesField ? (
              <div className="space-y-4 rounded-md border p-3">
                <p className="text-xs font-medium text-muted-foreground">
                  {t('content.customFields.title')}
                </p>
                {customFields.length === 0 ? (
                  <p className="text-xs text-muted-foreground">{t('content.customFields.empty')}</p>
                ) : (
                  customFields.map((f) => (
                    <ContentFieldInput
                      key={f.key}
                      field={asFieldDef(f)}
                      instanceConfig={instanceConfig}
                      value={customValues?.[f.key]}
                      onChange={(v) => setCustom(f.key, v)}
                    />
                  ))
                )}
              </div>
            ) : null}

            {!isNew ? (
              <div className="rounded-md border p-3">
                <button
                  type="button"
                  className="flex w-full items-center gap-2 text-sm font-medium"
                  onClick={() => setShowVersions((s) => !s)}
                >
                  <History className="h-4 w-4" />
                  {t('content.versions')} ({detail.data?.versions.length ?? 0})
                </button>
                {showVersions ? (
                  <ul className="mt-2 space-y-1 text-sm">
                    {detail.data?.versions.map((v) => (
                      <li key={v.id} className="flex items-center gap-2 text-muted-foreground">
                        <span>
                          {t('content.version')} {v.versionNo}
                        </span>
                        <span className="text-xs">{new Date(v.createdAt).toLocaleString()}</span>
                        {v.published ? <Badge tone="success">{t('content.published')}</Badge> : null}
                      </li>
                    ))}
                  </ul>
                ) : null}
              </div>
            ) : null}

            {!isNew ? (
              <div className="flex flex-wrap items-end gap-2 rounded-md border p-3">
                <div className="space-y-1.5">
                  <Label className="flex items-center gap-1 text-xs">
                    <CalendarClock className="h-3.5 w-3.5" />
                    {t('content.publishAt')}
                  </Label>
                  <Input
                    type="datetime-local"
                    value={scheduleAt}
                    onChange={(e) => setScheduleAt(e.target.value)}
                    className="w-56"
                  />
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  disabled={!scheduleAt || schedule.isPending}
                  onClick={() => schedule.mutate()}
                >
                  {t('actions.schedule')}
                </Button>
              </div>
            ) : null}
          </DialogBody>
        )}

        <DialogFooter className="flex-wrap">
          {!isNew && detail.data ? (
            detail.data.status === 'Published' ? (
              <Button variant="outline" onClick={() => action.mutate('unpublish')}>
                {t('actions.unpublish')}
              </Button>
            ) : (
              <Button variant="secondary" onClick={() => action.mutate('publish')}>
                {t('actions.publish')}
              </Button>
            )
          ) : null}
          <div className="flex-1" />
          <Button variant="outline" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button disabled={(isNew && !slug.trim()) || save.isPending} onClick={() => save.mutate()}>
            {t('actions.save')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
