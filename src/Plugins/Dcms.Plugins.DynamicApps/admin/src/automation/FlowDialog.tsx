import { useEffect, useMemo, useRef, useState } from 'react';
import { Plus, Sparkles, Undo2 } from 'lucide-react';
import { Button, Label, Textarea, toastApiError } from '@dcms/ui';
import { usePluginT } from '@dcms/plugin-ui';
import {
  apiNameOf, API_NAME, useComposeFlow, useExpressionLanguage, useFlowTypings, type ConfigIssue,
  type AppConfig, type ChangeOperation, type FlowActionInfo, type FlowDef, type FlowStep,
} from '../api';
import { ExpressionEditor } from './editor/ExpressionEditor';
import { contextFor } from './editor/expressionLanguage';
import { StepCard } from './StepCard';
import { freeStepId, toDraft, toStep, type StepDraft } from './steps';
import { CheckField, FormDialog, SelectField, TextField } from '../model/dialogs';

const EVENTS = ['row.created', 'row.updated', 'row.deleted', 'relation.created', 'relation.deleted', 'revision.published', 'visitor.registered', 'form.submitted', 'user.invited', 'user.activated', 'schedule', 'manual', 'flow.event'] as const;
type EventChoice = (typeof EVENTS)[number];

// Kept out of the translations: i18next would read its {{ }} as interpolation.
const STEP_EXAMPLE = '[{ "id": "notify", "action": "dcms.email.send@1", "input": { "to": "{{ row.email }}", "subject": "New {{ row.title }}" } }]';

const EXAMPLE: FlowStep[] = [
  { id: 'notify', action: 'dcms.notifications.raise@1', input: { title: 'New record', body: '{{ row.id }}' } },
];

const NO_ID = '00000000-0000-0000-0000-000000000000';

function eventChoiceOf(event: string | undefined): EventChoice {
  return event?.startsWith('flow.event.') ? 'flow.event' : ((event as EventChoice) ?? 'row.created');
}

function parseSteps(text: string): FlowStep[] | null {
  try {
    const value: unknown = JSON.parse(text);
    return Array.isArray(value) ? (value as FlowStep[]) : null;
  } catch {
    return null;
  }
}

/**
 * A flow: its trigger as a form, its condition as an expression, and its steps as cards — each
 * with its action, a condition, its input as JSON checked against the action's schema, and for
 * a script step, TypeScript typed from this app. All of the steps can still be edited as one
 * JSON document. The server validates everything when the draft is saved and when it is published.
 */
export function FlowDialog({ slug, config, flow, actions, pending, onCancel, onSave, onReload }: {
  slug: string;
  /** The working configuration, live: it refreshes when someone else — the assistant — changes the draft. */
  config: AppConfig;
  /** The flow as it was when the dialog opened. */
  flow?: FlowDef;
  actions: FlowActionInfo[];
  pending: boolean;
  onCancel: () => void;
  onSave: (operations: ChangeOperation[]) => void;
  /** Reopens the dialog on the draft's current version of the flow. */
  onReload: (flow: FlowDef) => void;
}) {
  const { t } = usePluginT();
  const initialEvent = eventChoiceOf(flow?.trigger.event);
  const [label, setLabel] = useState(flow?.displayName ?? '');
  const [api, setApi] = useState(flow?.apiName ?? '');
  const [event, setEvent] = useState<EventChoice>(initialEvent);
  const [customEvent, setCustomEvent] = useState(flow?.trigger.event.startsWith('flow.event.') ? flow.trigger.event.slice('flow.event.'.length) : '');
  const [tableId, setTableId] = useState(flow?.trigger.tableId ?? '');
  const [changed, setChanged] = useState<string[]>(flow?.trigger.changedFields ?? []);
  const [every, setEvery] = useState(flow?.trigger.everyMinutes?.toString() ?? '60');
  const [condition, setCondition] = useState(flow?.condition ?? '');
  const [enabled, setEnabled] = useState(flow?.enabled ?? true);
  const [drafts, setDrafts] = useState<StepDraft[]>(() => (flow?.steps ?? EXAMPLE).map(toDraft));
  // All of the steps as one JSON document, while that is how they are being edited.
  const [raw, setRaw] = useState<string | null>(null);
  const table = config.tables.find((x) => x.id === tableId);
  const isRow = event.startsWith('row.');
  // The draft's copy moved on while this was open: saving would overwrite that, so say so.
  const latest = flow ? config.flows.find((f) => f.id === flow.id) : undefined;
  const changedElsewhere = latest !== undefined && JSON.stringify(latest) !== JSON.stringify(flow);
  const language = useExpressionLanguage(slug);
  // One document per flow (a new flow has no id yet), so two flows never share an undo stack.
  const documentBase = `file:///dcms/dynamic-apps/${slug}/flows/${flow?.id ?? 'new'}`;

  // The flow's condition runs before any step, so it sees none of their outputs; each step's
  // condition sees the steps before it.
  const stepIds = drafts.map((d) => d.id).join('\u0000');
  const contexts = useMemo(() => {
    if (!language.data) return null;
    const ids = stepIds === '' ? [] : stepIds.split('\u0000');
    return ids.map((_, i) => contextFor(config, { event, tableId }, ids.slice(0, i), language.data!))
      .concat([contextFor(config, { event, tableId }, [], language.data)]);
  }, [config, event, tableId, language.data, stepIds]);
  const conditionContext = contexts?.at(-1) ?? null;

  const parsedSteps: FlowStep[] | null = raw !== null
    ? parseSteps(raw)
    : drafts.every((d) => toStep(d) !== null) ? drafts.map((d) => toStep(d)!) : null;

  const triggerEvent = event === 'flow.event' ? `flow.event.${customEvent}` : event;
  const value = {
    apiName: api,
    displayName: label.trim(),
    enabled,
    condition: condition.trim() || null,
    trigger: {
      event: triggerEvent,
      tableId: isRow ? tableId : null,
      changedFields: event === 'row.updated' ? changed : [],
      everyMinutes: event === 'schedule' ? Number(every) : null,
    },
    steps: parsedSteps,
  };

  // The flow as the form holds it now, unsaved, in the shape the server reads.
  const current = (): FlowDef => ({
    ...value,
    id: flow?.id ?? NO_ID,
    apiName: api || 'flow',
    condition: value.condition ?? undefined,
    trigger: { ...value.trigger, tableId: value.trigger.tableId || undefined, everyMinutes: value.trigger.everyMinutes ?? undefined },
    steps: parsedSteps ?? [],
  });

  /** Puts a whole flow into the form: what "Write with AI" wrote, or what Undo brings back. */
  const apply = (def: FlowDef) => {
    setLabel(def.displayName);
    // A saved flow keeps its api name: other flows and the site invoke it by that name.
    if (!flow) setApi(def.apiName);
    setEvent(eventChoiceOf(def.trigger.event));
    setCustomEvent(def.trigger.event.startsWith('flow.event.') ? def.trigger.event.slice('flow.event.'.length) : '');
    setTableId(def.trigger.tableId ?? '');
    setChanged(def.trigger.changedFields ?? []);
    setEvery(def.trigger.everyMinutes?.toString() ?? '60');
    setCondition(def.condition ?? '');
    setDrafts(def.steps.map(toDraft));
    setRaw(null);
    setFocusedScript(null);
    setTypings(null);
  };

  // "Write with AI": the request in the member's words, answered by the workspace's AI with a
  // whole flow that fills this form. Saving stays the member's call.
  const compose = useComposeFlow(slug);
  const [request, setRequest] = useState('');
  const [composing, setComposing] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const [aiIssues, setAiIssues] = useState<ConfigIssue[]>([]);
  const [before, setBefore] = useState<FlowDef | null>(null);
  const hasFlow = flow !== undefined || before !== null;
  const write = async () => {
    if (!request.trim() || composing) return;
    setComposing(true);
    try {
      const snapshot = current();
      const result = await compose(request.trim(), hasFlow ? snapshot : null);
      setBefore(snapshot);
      apply(result.flow);
      setNote(result.note);
      setAiIssues(result.issues.filter((i) => i.severity === 'error'));
      setRequest('');
    } catch (error) {
      toastApiError(error, t);
    } finally {
      setComposing(false);
    }
  };
  const undo = () => {
    if (!before) return;
    apply(before);
    setBefore(null);
    setNote(null);
    setAiIssues([]);
  };

  // The script being worked on gets declarations of its ctx: generated by the server from the
  // flow as it stands now (unsaved), typed against the draft's tables.
  const [focusedScript, setFocusedScript] = useState<string | null>(null);
  const [typings, setTypings] = useState<string | null>(null);
  const fetchTypings = useFlowTypings(slug);
  const fetchRef = useRef(fetchTypings);
  fetchRef.current = fetchTypings;
  const focused = drafts.find((d) => d.uid === focusedScript && d.code !== null);
  const typingsKey = focused && parsedSteps ? JSON.stringify([focused.id, value.trigger, parsedSteps.map((s) => [s.id, s.action, s.condition ?? null, s.input.table ?? null, s.input.args ?? null])]) : null;
  useEffect(() => {
    if (!typingsKey || !focused) return undefined;
    let live = true;
    const timer = setTimeout(() => {
      fetchRef.current(current(), focused.id)
        .then((d) => live && setTypings(d), () => {});
    }, 400);
    return () => {
      live = false;
      clearTimeout(timer);
    };
    // Keyed on what changes a script's ctx, not on every keystroke in its code.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [typingsKey]);

  const save = () => {
    onSave(flow
      ? [{ op: 'update', type: 'flow', target: flow.id, value }]
      : [{ op: 'create', type: 'flow', value: { ...value, condition: value.condition ?? undefined } }]);
  };

  const canSave = label.trim() !== '' && API_NAME.test(api) && parsedSteps !== null
    && (!isRow || tableId !== '') && (event !== 'flow.event' || /^[a-z][a-z0-9_.-]*$/.test(customEvent));

  const update = (uid: string, next: StepDraft) => setDrafts((all) => all.map((d) => (d.uid === uid ? next : d)));
  const move = (index: number, by: -1 | 1) => setDrafts((all) => {
    const next = [...all];
    [next[index], next[index + by]] = [next[index + by]!, next[index]!];
    return next;
  });
  const addStep = () => setDrafts((all) => [...all, toDraft({ id: freeStepId(all), action: actions[0]?.key ?? '', input: {} })]);
  const toRaw = () => setRaw(JSON.stringify(parsedSteps, null, 2));
  const toCards = () => {
    const steps = raw === null ? null : parseSteps(raw);
    if (!steps) return;
    setDrafts(steps.map(toDraft));
    setRaw(null);
  };

  return (
    <FormDialog title={t(flow ? 'automation.editFlow' : 'automation.newFlow')} pending={pending} canSave={canSave} onCancel={onCancel} onSave={save} wide>
      {changedElsewhere ? (
        <div role="status" className="flex flex-wrap items-center gap-2 rounded-md border border-amber-500/40 bg-amber-500/10 p-3 text-sm">
          <p className="min-w-0 flex-1">{t('automation.changedElsewhere')}</p>
          <Button type="button" size="sm" variant="outline" onClick={() => onReload(latest!)}>{t('automation.reload')}</Button>
        </div>
      ) : null}
      <section className="space-y-2 rounded-md border bg-muted/30 p-3" aria-label={t('automation.aiTitle')}>
        <Label htmlFor="flow-ai" className="flex items-center gap-1.5">
          <Sparkles className="size-4 text-primary" aria-hidden /> {t(hasFlow ? 'automation.aiChangeLabel' : 'automation.aiNewLabel')}
        </Label>
        <Textarea id="flow-ai" rows={2} value={request} placeholder={t('automation.aiPlaceholder')} disabled={composing}
          onChange={(e) => setRequest(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
              e.preventDefault();
              void write();
            }
          }} />
        <div className="flex flex-wrap items-center gap-2">
          <Button type="button" size="sm" disabled={!request.trim() || composing} onClick={() => void write()}>
            <Sparkles className="size-4" aria-hidden /> {composing ? t('automation.aiWriting') : t(hasFlow ? 'automation.aiChange' : 'automation.aiWrite')}
          </Button>
          {before ? (
            <Button type="button" size="sm" variant="ghost" disabled={composing} onClick={undo}>
              <Undo2 className="size-4" aria-hidden /> {t('automation.aiUndo')}
            </Button>
          ) : null}
        </div>
        {note ? <p className="text-sm" role="status">{note}</p> : null}
        {aiIssues.length > 0 ? (
          <div className="text-xs text-destructive">
            <p>{t('automation.aiIssues')}</p>
            <ul className="list-disc pl-4">{aiIssues.map((i, n) => <li key={n}>{i.path}: {i.message}</li>)}</ul>
          </div>
        ) : null}
        {!note && aiIssues.length === 0 ? <p className="text-xs text-muted-foreground">{t('automation.aiHint')}</p> : null}
      </section>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <TextField id="flow-label" label={t('form.displayName')} value={label}
          onChange={(v) => { setLabel(v); if (!flow) setApi(apiNameOf(v)); }} />
        <TextField id="flow-api" label={t('form.apiName')} value={api} onChange={setApi} mono />
      </div>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <SelectField id="flow-event" label={t('automation.trigger')} value={event} onChange={setEvent}
          options={EVENTS.map((value) => ({ value, label: t(`events.${value.replace('.', '_')}`) }))} />
        {isRow ? (
          <SelectField id="flow-table" label={t('automation.table')} value={tableId} onChange={setTableId}
            options={config.tables.map((x) => ({ value: x.id, label: x.displayName }))} />
        ) : null}
        {event === 'schedule' ? (
          <TextField id="flow-every" label={t('automation.everyMinutes')} value={every} onChange={setEvery} type="number" />
        ) : null}
        {event === 'flow.event' ? (
          <TextField id="flow-custom" label={t('automation.eventName')} value={customEvent} onChange={setCustomEvent} mono />
        ) : null}
      </div>
      {event === 'row.updated' && table ? (
        <fieldset className="space-y-1">
          <legend className="text-sm font-medium">{t('automation.changedFields')}</legend>
          <div className="grid grid-cols-1 gap-1 sm:grid-cols-3">
            {table.fields.map((f) => (
              <CheckField key={f.id} id={`chg-${f.id}`} label={f.displayName} checked={changed.includes(f.id)}
                onChange={(on) => setChanged((ids) => (on ? [...ids, f.id] : ids.filter((x) => x !== f.id)))} />
            ))}
          </div>
        </fieldset>
      ) : null}
      <ExpressionEditor uri={`${documentBase}/condition.dexpr`} slug={slug} label={t('automation.condition')}
        value={condition} onChange={setCondition} context={conditionContext} hint={t('automation.conditionHint')} />

      <div className="flex flex-wrap items-center gap-2">
        <h3 className="mr-auto text-sm font-semibold">{t('automation.stepsTitle')}</h3>
        {raw === null ? (
          <Button type="button" variant="outline" size="sm" disabled={parsedSteps === null} onClick={toRaw}>{t('automation.rawJson')}</Button>
        ) : (
          <Button type="button" variant="outline" size="sm" disabled={parsedSteps === null} onClick={toCards}>{t('automation.cards')}</Button>
        )}
      </div>
      {raw !== null ? (
        <TextField id="flow-steps" label={t('automation.stepsJson')} value={raw} onChange={setRaw} multiline mono
          invalid={parsedSteps === null}
          hint={`${t('automation.stepsHint', { actions: actions.map((a) => a.key).join(', ') })} ${STEP_EXAMPLE}`} />
      ) : (
        <div className="space-y-3">
          {drafts.map((draft, index) => (
            <StepCard key={draft.uid} draft={draft} index={index} count={drafts.length} slug={slug} documentBase={documentBase}
              actions={actions} context={contexts?.[index] ?? null} typings={draft.uid === focusedScript ? typings : null}
              onChange={(next) => update(draft.uid, next)} onMove={(by) => move(index, by)}
              onRemove={() => setDrafts((all) => all.filter((d) => d.uid !== draft.uid))}
              onFocusScript={() => {
                if (focusedScript !== draft.uid) {
                  setTypings(null);
                  setFocusedScript(draft.uid);
                }
              }} />
          ))}
          <Button type="button" variant="outline" size="sm" onClick={addStep}>
            <Plus className="size-4" /> {t('automation.addStep')}
          </Button>
        </div>
      )}
      <CheckField id="flow-enabled" label={t('form.enabled')} checked={enabled} onChange={setEnabled} />
    </FormDialog>
  );
}
