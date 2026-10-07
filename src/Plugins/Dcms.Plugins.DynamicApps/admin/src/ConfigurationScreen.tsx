import { useMemo, useState } from 'react';
import { CheckCircle2, CircleDot, History, Rocket, Sparkles, Trash2 } from 'lucide-react';
import {
  Badge,
  Button,
  CenteredSpinner,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  toast,
} from '@dcms/ui';
import {
  instancePath,
  useCan,
  usePluginHost,
  usePluginAiContext,
  usePluginApi,
  usePluginT,
  type PluginScreenProps,
} from '@dcms/plugin-ui';
import { ConfirmDialog } from './ConfirmDialog';
import { useAppState, useModelAction, useWorkingConfig, type ValidationResultLike } from './api';
import { ModelTab } from './model/ModelTab';
import { AutomationTab } from './automation/AutomationTab';
import { AccessTab } from './AccessTab';
import { RevisionsTab } from './RevisionsTab';
import { PublishDialog } from './PublishDialog';

/**
 * The application's control plane (instance screen "configuration"): its tables, automations,
 * public access and revisions. Everything here edits the draft — the live app changes only when
 * the draft is published — and the assistant beside it works on the same draft, so the two can
 * be used together.
 */
export function ConfigurationScreen({ instance }: PluginScreenProps) {
  const { t } = usePluginT();
  const slug = instance!.slug;
  const api = usePluginApi();
  const mayEdit = useCan('model-write');
  const mayPublish = useCan('publish');
  const assistant = usePluginHost().assistant;
  // The assistant can only work on the app once its AI tools are on; offer that first.
  const [enablingAi, setEnablingAi] = useState(false);
  const [aiPending, setAiPending] = useState(false);
  const askAi = () => (instance!.aiToolsEnabled === false ? setEnablingAi(true) : assistant?.open());
  const enableAi = async () => {
    setAiPending(true);
    try {
      await assistant?.enableTools(instance!.id);
      setEnablingAi(false);
      assistant?.open();
    } catch {
      toast.error(t('ai.enableFailed'));
    } finally {
      setAiPending(false);
    }
  };
  const [tab, setTab] = useState('model');
  const [selection, setSelection] = useState<string | null>(null);
  const [publishing, setPublishing] = useState(false);
  const [discarding, setDiscarding] = useState(false);

  const state = useAppState(slug);
  const working = useWorkingConfig(slug, state.data);
  const draft = state.data?.draft ?? null;
  const published = state.data?.published ?? null;

  const context = useMemo(
    () => ({
      area: 'data-platform',
      summary: `the ${instance!.name} app's configuration (Dynamic Apps instance "${slug}"), ${tab} tab; `
        + (draft ? `draft revision ${draft.number} is open` : published ? `revision ${published.number} is live, no draft open` : 'nothing published yet'),
      selection: [instance!.id, ...(selection ? [selection] : []), ...(draft ? [draft.id] : [])],
    }),
    [instance, slug, tab, draft, published, selection],
  );
  usePluginAiContext(context);

  const validate = useModelAction(slug, () =>
    api.post<ValidationResultLike>(instancePath(slug, '/_model/draft/validate')));
  const discard = useModelAction(slug, (hash: string) =>
    api.post(instancePath(slug, '/_model/draft/discard'), { expectedHash: hash }));

  if (state.isLoading || working.isLoading) return <CenteredSpinner />;

  const config = working.data?.config ?? null;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3 rounded-lg border bg-card p-3">
        <div className="flex min-w-0 flex-1 flex-wrap items-center gap-2 text-sm">
          {draft ? (
            <>
              <Badge tone="secondary" className="gap-1">
                <CircleDot className="size-3" aria-hidden /> {t('status.draft', { number: draft.number })}
              </Badge>
              {draft.source === 'ai' ? <Badge tone="outline">{t('status.byAssistant')}</Badge> : null}
              {draft.validatedAt ? (
                <span className="inline-flex items-center gap-1 text-muted-foreground">
                  <CheckCircle2 className="size-3.5" aria-hidden /> {t('status.validated')}
                </span>
              ) : null}
            </>
          ) : (
            <span className="text-muted-foreground">{t(published ? 'status.noDraft' : 'status.empty')}</span>
          )}
          {published ? <Badge tone="outline">{t('status.live', { number: published.number })}</Badge> : null}
        </div>
        <div className="flex flex-wrap gap-2">
          {assistant ? (
            <Button variant="outline" size="sm" onClick={askAi}>
              <Sparkles className="size-4" aria-hidden /> {t('actions.askAi')}
            </Button>
          ) : null}
          {draft && mayEdit ? (
            <>
              <Button variant="outline" size="sm" disabled={validate.isPending}
                onClick={() => validate.mutate(undefined, {
                  onSuccess: (result) => result.valid
                    ? toast.success(t('validate.valid'))
                    : toast.warning(t('validate.invalid', { count: result.issues.filter((i) => i.severity === 'error').length })),
                })}>
                <CheckCircle2 className="size-4" aria-hidden /> {t('actions.validate')}
              </Button>
              <Button variant="outline" size="sm" onClick={() => setDiscarding(true)}>
                <Trash2 className="size-4" aria-hidden /> {t('actions.discard')}
              </Button>
            </>
          ) : null}
          {draft && mayPublish ? (
            <Button size="sm" onClick={() => setPublishing(true)}>
              <Rocket className="size-4" aria-hidden /> {t('actions.reviewPublish')}
            </Button>
          ) : null}
        </div>
      </div>

      {!instance!.enabled ? <p className="text-sm text-muted-foreground">{t('status.instanceOff')}</p> : null}

      <Tabs value={tab} onValueChange={setTab}>
        <TabsList>
          <TabsTrigger value="model">{t('tabs.model')}</TabsTrigger>
          <TabsTrigger value="automation">{t('tabs.automation')}</TabsTrigger>
          <TabsTrigger value="access">{t('tabs.access')}</TabsTrigger>
          <TabsTrigger value="revisions">
            <History className="size-4" aria-hidden /> {t('tabs.revisions')}
          </TabsTrigger>
        </TabsList>
        <TabsContent value="model">
          <ModelTab slug={slug} config={config} editable={mayEdit} onSelect={setSelection} />
        </TabsContent>
        <TabsContent value="automation">
          <AutomationTab slug={slug} config={config} editable={mayEdit} onSelect={setSelection} />
        </TabsContent>
        <TabsContent value="access">
          <AccessTab slug={slug} config={config} editable={mayEdit} published={published !== null} />
        </TabsContent>
        <TabsContent value="revisions">
          <RevisionsTab slug={slug} mayRollback={mayPublish && !draft} />
        </TabsContent>
      </Tabs>

      {draft ? <PublishDialog slug={slug} open={publishing} onOpenChange={setPublishing} draft={draft} /> : null}
      <ConfirmDialog
        open={enablingAi}
        onOpenChange={setEnablingAi}
        title={t('ai.enableTitle')}
        description={t('ai.enableDescription')}
        confirmLabel={t('ai.enable')}
        pending={aiPending}
        onConfirm={() => void enableAi()}
      />
      <ConfirmDialog
        open={discarding}
        onOpenChange={setDiscarding}
        title={t('discard.title', { number: draft?.number })}
        description={t('discard.description')}
        confirmLabel={t('actions.discard')}
        pending={discard.isPending}
        onConfirm={() => draft && discard.mutate(draft.hash, { onSuccess: () => setDiscarding(false) })}
      />
    </div>
  );
}
