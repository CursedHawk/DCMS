import { useMutation, useQueryClient } from '@tanstack/react-query';
import { CalendarClock, History } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  CenteredSpinner,
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  toastApiError,
} from '@dcms/admin-ui';
import { api } from '../../lib/api';
import {
  type ContentFieldDef,
  type ContentTypeDef,
  type CustomFieldDef,
  customFieldsOf,
  parseInstanceConfig,
  usePluginInstances,
} from '../plugins/api';
import { ContentFieldInput } from './ContentFieldInput';
import { useContentItem, useTagVocabularies } from './api';

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

  /*
   * One request per Tags field, keyed by field name — a content type can have more
   * than one (Events has "genres"), and each has its own vocabulary. The list is
   * only fetched for types that actually declare a Tags field.
   */
  const tagFields = contentType.fields.filter((f) => f.type === 'Tags');
  // The tenant's own tag fields count too. They are nested under `valuesField`,
  // so they are addressed by the dotted path the vocabulary query reports — the
  // reason a custom "genres" field used to be the one tag input in the app with
  // no suggestions at all.
  const customTagFields = customFields.filter((f) => f.type === 'tags');
  const tagSuggestions = useTagVocabularies(instanceId, contentType.name, [
    ...tagFields.map((f) => f.name),
    ...customTagFields.map((f) => (valuesField ? `${valuesField}.${f.key}` : f.key)),
  ]);

  const pendingSchedule = detail.data?.scheduledPublishAt ?? null;

  const invalidate = async () => {
    await qc.invalidateQueries({ queryKey: ['content', instanceId] });
    if (itemId) await qc.invalidateQueries({ queryKey: ['content-item', itemId] });
  };

  // Creating returns the new id; updating returns the new version. Typed as the
  // union so saveAndPublish can pick the id out of the create case.
  const save = useMutation<{ id?: string; versionId?: string }>({
    mutationFn: () =>
      isNew
        ? api.post<{ id: string }>('/admin/content', {
            pluginInstanceId: instanceId,
            contentType: contentType.name,
            slug: slug.trim(),
            data,
          })
        : api.put<{ versionId: string }>(`/admin/content/${itemId}`, { data }),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await invalidate();
      onSaved();
    },
    onError: (e) => toastApiError(e, t),
  });

  // Publishing carries the current form state, so it is "save and publish" in one
  // request: the server appends a draft version and pins it, atomically. Without
  // that, clicking Publish after typing would ship the previously *saved* revision.
  const action = useMutation({
    mutationFn: (verb: 'publish' | 'unpublish') =>
      api.post(`/admin/content/${itemId}/${verb}`, verb === 'publish' ? { data } : undefined),
    onSuccess: async (_d, verb) => {
      toast.success(verb === 'publish' ? t('content.published') : t('content.draft'));
      await invalidate();
      if (verb === 'publish') onSaved();
    },
    onError: (e) => toastApiError(e, t),
  });

  // Same reasoning as publish: schedule what is on screen, not what was last saved.
  const schedule = useMutation({
    mutationFn: () =>
      api.post(`/admin/content/${itemId}/schedule`, {
        publishAt: new Date(scheduleAt).toISOString(),
        data,
      }),
    onSuccess: async () => {
      toast.success(t('content.scheduled'));
      setScheduleAt('');
      await invalidate();
    },
    onError: (e) => toastApiError(e, t),
  });

  const cancelSchedule = useMutation({
    mutationFn: () => api.del(`/admin/content/${itemId}/schedule`),
    onSuccess: async () => {
      toast.success(t('content.scheduleCancelled'));
      await invalidate();
    },
    onError: (e) => toastApiError(e, t),
  });

  /**
   * A new item has no id yet, so it must be created before it can be published;
   * for an existing item the publish request carries the edits and does both in
   * one transaction. Both paths publish what is currently on screen.
   */
  async function saveAndPublish() {
    if (isNew) {
      const created = await save.mutateAsync().catch(() => null);
      const newId = created?.id;
      if (!newId) return;
      try {
        await api.post(`/admin/content/${newId}/publish`, { data });
        toast.success(t('content.published'));
        await invalidate();
        onSaved();
      } catch (e) {
        toastApiError(e, t);
      }
      return;
    }
    action.mutate('publish');
  }

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
                  tagSuggestions={tagSuggestions[f.name]}
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
                      tagSuggestions={
                        tagSuggestions[valuesField ? `${valuesField}.${f.key}` : f.key]
                      }
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
              <div className="space-y-2 rounded-md border p-3">
                {/* A queued publish changes this item without anyone touching it, so
                    it has to be visible — and cancellable — from the editor. */}
                {pendingSchedule ? (
                  <div className="flex flex-wrap items-center gap-2 rounded-md bg-accent/40 px-2.5 py-2 text-sm">
                    <CalendarClock className="h-4 w-4 text-muted-foreground" />
                    <span>
                      {t('content.scheduledFor', {
                        when: new Date(pendingSchedule).toLocaleString(),
                      })}
                    </span>
                    <div className="flex-1" />
                    <Button
                      variant="ghost"
                      size="sm"
                      disabled={cancelSchedule.isPending}
                      onClick={() => cancelSchedule.mutate()}
                    >
                      {t('content.cancelSchedule')}
                    </Button>
                  </div>
                ) : null}

                <div className="flex flex-wrap items-end gap-2">
                  <div className="space-y-1.5">
                    <Label className="flex items-center gap-1 text-xs">
                      <CalendarClock className="h-3.5 w-3.5" />
                      {pendingSchedule ? t('content.rescheduleAt') : t('content.publishAt')}
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
                <p className="text-xs text-muted-foreground">{t('content.scheduleHint')}</p>
              </div>
            ) : null}
          </DialogBody>
        )}

        <DialogFooter className="flex-wrap">
          {!isNew && detail.data?.status === 'Published' ? (
            <Button variant="outline" onClick={() => action.mutate('unpublish')}>
              {t('actions.unpublish')}
            </Button>
          ) : null}
          <div className="flex-1" />
          <Button variant="outline" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button
            variant="secondary"
            disabled={(isNew && !slug.trim()) || save.isPending}
            onClick={() => save.mutate()}
          >
            {t('actions.save')}
          </Button>
          {/*
            A new item has no id to publish against, so it is saved first and the
            publish follows; for an existing item one request does both. Either way
            what gets published is what is on screen.
          */}
          <Button
            disabled={(isNew && !slug.trim()) || save.isPending || action.isPending}
            onClick={() => saveAndPublish()}
          >
            {t('content.saveAndPublish')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
