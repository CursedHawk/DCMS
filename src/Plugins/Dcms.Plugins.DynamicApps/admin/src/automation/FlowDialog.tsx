import { useState } from 'react';
import { usePluginT } from '@dcms/plugin-ui';
import { apiNameOf, API_NAME, type AppConfig, type ChangeOperation, type FlowActionInfo, type FlowDef, type FlowStep } from '../api';
import { CheckField, FormDialog, SelectField, TextField } from '../model/dialogs';

const EVENTS = ['row.created', 'row.updated', 'row.deleted', 'relation.created', 'relation.deleted', 'revision.published', 'visitor.registered', 'form.submitted', 'user.invited', 'user.activated', 'schedule', 'manual', 'flow.event'] as const;
type EventChoice = (typeof EVENTS)[number];

// Kept out of the translations: i18next would read its {{ }} as interpolation.
const STEP_EXAMPLE = '[{ "id": "notify", "action": "dcms.email.send@1", "input": { "to": "{{ row.email }}", "subject": "New {{ row.title }}" } }]';

const EXAMPLE: FlowStep[] = [
  { id: 'notify', action: 'dcms.notifications.raise@1', input: { title: 'New record', body: '{{ row.id }}' } },
];

/**
 * A flow: its trigger as a form, its condition as an expression, and its steps as JSON — the
 * one shape that holds an action's input with its {{ }} templates without a form per action.
 * The server validates everything when the draft is saved and when it is published.
 */
export function FlowDialog({ config, flow, actions, pending, onCancel, onSave }: {
  config: AppConfig;
  flow?: FlowDef;
  actions: FlowActionInfo[];
  pending: boolean;
  onCancel: () => void;
  onSave: (operations: ChangeOperation[]) => void;
}) {
  const { t } = usePluginT();
  const initialEvent: EventChoice = flow?.trigger.event.startsWith('flow.event.') ? 'flow.event' : ((flow?.trigger.event as EventChoice) ?? 'row.created');
  const [label, setLabel] = useState(flow?.displayName ?? '');
  const [api, setApi] = useState(flow?.apiName ?? '');
  const [event, setEvent] = useState<EventChoice>(initialEvent);
  const [customEvent, setCustomEvent] = useState(flow?.trigger.event.startsWith('flow.event.') ? flow.trigger.event.slice('flow.event.'.length) : '');
  const [tableId, setTableId] = useState(flow?.trigger.tableId ?? '');
  const [changed, setChanged] = useState<string[]>(flow?.trigger.changedFields ?? []);
  const [every, setEvery] = useState(flow?.trigger.everyMinutes?.toString() ?? '60');
  const [condition, setCondition] = useState(flow?.condition ?? '');
  const [enabled, setEnabled] = useState(flow?.enabled ?? true);
  const [steps, setSteps] = useState(JSON.stringify(flow?.steps ?? EXAMPLE, null, 2));
  const table = config.tables.find((x) => x.id === tableId);
  const isRow = event.startsWith('row.');

  let parsedSteps: FlowStep[] | null = null;
  try {
    const value = JSON.parse(steps);
    parsedSteps = Array.isArray(value) ? value : null;
  } catch {
    parsedSteps = null;
  }

  const save = () => {
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
    onSave(flow
      ? [{ op: 'update', type: 'flow', target: flow.id, value }]
      : [{ op: 'create', type: 'flow', value: { ...value, condition: value.condition ?? undefined } }]);
  };

  const canSave = label.trim() !== '' && API_NAME.test(api) && parsedSteps !== null
    && (!isRow || tableId !== '') && (event !== 'flow.event' || /^[a-z][a-z0-9_.-]*$/.test(customEvent));

  return (
    <FormDialog title={t(flow ? 'automation.editFlow' : 'automation.newFlow')} pending={pending} canSave={canSave} onCancel={onCancel} onSave={save} wide>
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
      <TextField id="flow-condition" label={t('automation.condition')} value={condition} onChange={setCondition} mono
        hint={t('automation.conditionHint')} />
      <TextField id="flow-steps" label={t('automation.stepsJson')} value={steps} onChange={setSteps} multiline mono
        invalid={parsedSteps === null}
        hint={`${t('automation.stepsHint', { actions: actions.map((a) => a.key).join(', ') })} ${STEP_EXAMPLE}`} />
      <CheckField id="flow-enabled" label={t('form.enabled')} checked={enabled} onChange={setEnabled} />
    </FormDialog>
  );
}
