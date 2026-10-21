import { useContext, useEffect, useId, useState, type FormEvent, type ReactNode } from 'react';
import { useInRouterContext, useNavigate } from 'react-router';
import { DataClientContext, DataError, useItem } from './data';
import type { ModelField, ModelTable } from './dataTable';
import type { ComponentDefinition, ComponentRenderProps } from './registry';
import { useRenderMode } from './renderMode';

type Props = Record<string, unknown>;
type Navigate = ((to: string) => void) | null;

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' && value.trim() ? value : fallback;
}

/** `crm`, `deals`: a slug or api name, else nothing is asked for. */
const NAME = /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/;

/** The field types a visitor can fill in; anything else (json, media) is left out of the form. */
const EDITABLE = new Set(['text', 'longText', 'email', 'url', 'integer', 'decimal', 'boolean', 'date', 'dateTime', 'choice', 'multiChoice', 'lookup']);

/**
 * A form that creates a record in a Dynamic Apps table, or — on that table's detail page, with
 * `mode: edit` — changes the record the page shows.
 *
 * <p>It writes through the app's public API (`POST /api/{app}/data/{table}`, `PATCH …/{id}` with
 * the version it read, so a record someone else changed meanwhile is refused rather than
 * overwritten). What a visitor may do is the app's access rules' decision, not this component's:
 * a table that does not let the public create records refuses, and the form says so.</p>
 *
 * <p>The fields come from the app's model, so a field added to the table appears in the form
 * without anyone editing the page. On the canvas and in the preview it draws, and never saves.</p>
 */
export function RecordForm(render: ComponentRenderProps<Props>) {
  // useNavigate throws outside a router (the canvas has none), so only call it inside one.
  return useInRouterContext() ? <WithNavigate {...render} /> : <RecordFormBody {...render} navigate={null} />;
}

function WithNavigate(render: ComponentRenderProps<Props>) {
  const navigate = useNavigate();
  return <RecordFormBody {...render} navigate={(to) => void navigate(to)} />;
}

function RecordFormBody({ props, navigate }: ComponentRenderProps<Props> & { navigate: Navigate }) {
  const mode = useRenderMode();
  const client = useContext(DataClientContext);
  const scope = useItem();
  const app = text(props.instance).trim();
  const tableName = text(props.table).trim();
  const editing = props.mode === 'edit';
  const configured = NAME.test(app) && NAME.test(tableName);
  const record = editing ? scope?.item.data : undefined;

  const [table, setTable] = useState<ModelTable | 'missing' | null>(null);
  useEffect(() => {
    if (!configured) return;
    let live = true;
    setTable(null);
    client.get(`/api/${app}/_model`).then(
      (json) => {
        const found = ((json as { tables?: ModelTable[] })?.tables ?? []).find((t) => t.apiName === tableName);
        if (live) setTable(found ?? 'missing');
      },
      () => live && setTable('missing'),
    );
    return () => {
      live = false;
    };
  }, [client, app, tableName, configured]);

  const wanted = text(props.fields).split(',').map((f) => f.trim()).filter(Boolean);
  const fields =
    table && table !== 'missing'
      ? (wanted.length ? wanted.map((name) => table.fields.find((f) => f.apiName === name)) : table.fields)
          .filter((f): f is ModelField => !!f && EDITABLE.has(f.type) && !f.readOnly)
      : [];

  const [state, setState] = useState<'idle' | 'sending' | 'sent' | 'error'>('idle');
  const [message, setMessage] = useState<string | null>(null);

  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const element = e.currentTarget;
    if (mode === 'edit' || !configured || fields.length === 0) return;
    if (editing && !scope) return;
    const values = readValues(element, fields, editing);
    setState('sending');
    setMessage(null);
    try {
      let saved: unknown;
      if (editing) {
        if (!client.send) throw new Error('Changes are saved on the published site.');
        const id = scope!.item.id;
        saved = await client.send('PATCH', `/api/${app}/data/${tableName}/${encodeURIComponent(id)}`, {
          ...values,
          ...(typeof record?.version === 'number' ? { version: record.version } : {}),
        });
      } else {
        saved = await client.post(`/api/${app}/data/${tableName}`, values);
        element.reset();
      }
      setState('sent');
      const after = text(props.afterSave).trim();
      const id = (saved as { id?: unknown } | undefined)?.id ?? scope?.item.id;
      if (after.startsWith('/') && navigate) navigate(typeof id === 'string' ? after.replace(/:id\b/g, encodeURIComponent(id)) : after);
    } catch (err) {
      setState('error');
      const body = err instanceof DataError ? (err.body as { error?: string } | undefined) : undefined;
      const status = err instanceof DataError ? err.status : 0;
      setMessage(
        body?.error ??
          (status === 401 ? 'Sign in to save this.' : status === 403 ? 'You may not save records here.' : status === 409 ? 'Someone changed this meanwhile. Reload and try again.' : (err as Error).message || null),
      );
    }
  };

  if (!configured) {
    return mode === 'edit' ? <p className="dcms-editor-note">Choose the app and the table this form writes to.</p> : null;
  }
  if (table === 'missing') {
    return mode === 'edit' ? (
      <p className="dcms-editor-note">
        “{tableName}” is not a table the site can write to in the app “{app}”: it does not exist, is not published yet, or the app does not open it to the
        public.
      </p>
    ) : null;
  }
  if (editing && !scope && mode === 'edit') {
    return <p className="dcms-editor-note">An edit form changes the record its page shows: put it on a detail page over {app}/{tableName}.</p>;
  }

  return (
    <form className="dcms-form" onSubmit={submit} noValidate={mode === 'edit'}>
      <div className="dcms-form-fields">
        {fields.map((f) => (
          <RecordField key={`${f.apiName}:${scope?.item.id ?? ''}`} field={f} app={app} value={record?.[f.apiName]} />
        ))}
      </div>
      {state === 'sent' && !editing ? (
        <p className="dcms-form-status" role="status">
          {text(props.successMessage, 'Saved.')}
        </p>
      ) : null}
      <button type="submit" className="dcms-button dcms-button-primary dcms-button-md" disabled={state === 'sending' || table === null}>
        {text(props.submitLabel, editing ? 'Save changes' : 'Save')}
      </button>
      {state === 'sent' && editing ? (
        <p className="dcms-form-status" role="status">
          {text(props.successMessage, 'Saved.')}
        </p>
      ) : null}
      {state === 'error' && (
        <p className="dcms-form-status dcms-form-error" role="alert">
          {message ?? 'Saving failed. Please try again.'}
        </p>
      )}
    </form>
  );
}

/**
 * The form's values in the shapes the app stores. An empty field is left out of a new record and
 * cleared (null) on an existing one, so editing can empty a field but creating never sends blanks.
 */
function readValues(form: HTMLFormElement, fields: readonly ModelField[], editing: boolean): Record<string, unknown> {
  const data = new FormData(form);
  const out: Record<string, unknown> = {};
  for (const f of fields) {
    if (f.type === 'boolean') {
      out[f.apiName] = (form.elements.namedItem(f.apiName) as HTMLInputElement | null)?.checked ?? false;
      continue;
    }
    if (f.type === 'multiChoice') {
      out[f.apiName] = data.getAll(f.apiName).map(String);
      continue;
    }
    const raw = String(data.get(f.apiName) ?? '').trim();
    if (!raw) {
      if (editing) out[f.apiName] = null;
      continue;
    }
    out[f.apiName] = f.type === 'integer' ? parseInt(raw, 10) : f.type === 'decimal' ? Number(raw) : f.type === 'dateTime' ? new Date(raw).toISOString() : raw;
  }
  return out;
}

function RecordField({ field, app, value }: { field: ModelField; app: string; value: unknown }) {
  const id = useId();
  const label = (
    <>
      {field.displayName}
      {field.required ? ' *' : ''}
    </>
  );
  const shown = value === null || value === undefined ? '' : value;

  switch (field.type) {
    case 'boolean':
      return (
        <div className="dcms-field">
          <label className="dcms-field-check" htmlFor={id}>
            <input id={id} name={field.apiName} type="checkbox" defaultChecked={shown === true} /> {field.displayName}
          </label>
        </div>
      );
    case 'multiChoice': {
      const picked = Array.isArray(shown) ? shown.map(String) : [];
      return (
        <fieldset className="dcms-field dcms-field-group">
          <legend>{label}</legend>
          {(field.options ?? []).map((option) => (
            <label key={option} className="dcms-field-check">
              <input type="checkbox" name={field.apiName} value={option} defaultChecked={picked.includes(option)} /> {option}
            </label>
          ))}
        </fieldset>
      );
    }
    case 'choice':
      return (
        <div className="dcms-field">
          <label htmlFor={id}>{label}</label>
          <select id={id} name={field.apiName} required={field.required} defaultValue={String(shown)}>
            <option value="">Choose…</option>
            {(field.options ?? []).map((option) => (
              <option key={option} value={option}>
                {option}
              </option>
            ))}
          </select>
        </div>
      );
    case 'lookup':
      return <LookupField id={id} field={field} app={app} value={shown} label={label} />;
    case 'longText':
      return (
        <div className="dcms-field">
          <label htmlFor={id}>{label}</label>
          <textarea id={id} name={field.apiName} required={field.required} rows={4} defaultValue={String(shown)} />
        </div>
      );
    default: {
      const type =
        field.type === 'integer' || field.type === 'decimal'
          ? 'number'
          : field.type === 'date'
            ? 'date'
            : field.type === 'dateTime'
              ? 'datetime-local'
              : field.type === 'email'
                ? 'email'
                : field.type === 'url'
                  ? 'url'
                  : 'text';
      const initial = field.type === 'dateTime' ? localInput(shown) : String(shown);
      return (
        <div className="dcms-field">
          <label htmlFor={id}>{label}</label>
          <input id={id} name={field.apiName} type={type} required={field.required} step={field.type === 'decimal' ? 'any' : undefined} defaultValue={initial} />
        </div>
      );
    }
  }
}

/** A lookup as a choice of the target table's records, by their primary field. */
function LookupField({ id, field, app, value, label }: { id: string; field: ModelField; app: string; value: unknown; label: ReactNode }) {
  const client = useContext(DataClientContext);
  const [options, setOptions] = useState<{ id: string; label: string }[] | null>(null);
  const current = typeof value === 'string' ? value : value && typeof value === 'object' ? String((value as { id?: unknown }).id ?? '') : '';
  useEffect(() => {
    if (!field.target || !NAME.test(field.target)) return;
    let live = true;
    // ponytail: the first 100 by the API's default order; a search box when tables grow past it.
    client.get(`/api/${app}/data/${field.target}?pageSize=100`).then(
      (json) => {
        const items = ((json as { items?: Record<string, unknown>[] })?.items ?? []).filter((r) => typeof r.id === 'string');
        const name = (r: Record<string, unknown>) => String(r.name ?? r.title ?? Object.values(r).find((v) => typeof v === 'string' && v !== r.id) ?? r.id);
        if (live) setOptions(items.map((r) => ({ id: r.id as string, label: name(r) })));
      },
      () => live && setOptions([]),
    );
    return () => {
      live = false;
    };
  }, [client, app, field.target]);
  return (
    <div className="dcms-field">
      <label htmlFor={id}>{label}</label>
      <select id={id} name={field.apiName} required={field.required} defaultValue={current} key={options ? 'ready' : 'loading'}>
        <option value="">{options ? 'Choose…' : 'Loading…'}</option>
        {(options ?? []).map((o) => (
          <option key={o.id} value={o.id}>
            {o.label}
          </option>
        ))}
      </select>
    </div>
  );
}

export const RECORD_FORM_COMPONENTS: readonly ComponentDefinition[] = [
  {
    type: 'dcms.record-form',
    version: 1,
    label: 'Record form',
    description:
      'A form that adds a record to one of your Dynamic Apps tables — or, on a detail page over that table with “Edit the page’s record”, changes it. Its fields come from the table.',
    category: 'Data',
    keywords: ['form', 'record', 'create', 'edit', 'dynamic apps', 'crm', 'add', 'update'],
    component: RecordForm,
    props: [
      { kind: 'text', name: 'instance', label: 'App', required: true, maxLength: 64, group: 'data', description: 'The Dynamic Apps instance, e.g. “crm”.' },
      {
        kind: 'text',
        name: 'table',
        label: 'Table',
        required: true,
        maxLength: 64,
        group: 'data',
        description: 'The table’s API name, e.g. “deals”. The app’s public access decides who may add or change its records.',
      },
      {
        kind: 'select',
        name: 'mode',
        label: 'Does',
        default: 'create',
        group: 'data',
        options: [
          { value: 'create', label: 'Add a new record' },
          { value: 'edit', label: 'Edit the page’s record' },
        ],
        description: 'Editing needs a detail page over the same table: the form changes the record that page shows.',
      },
      {
        kind: 'text',
        name: 'fields',
        label: 'Fields',
        maxLength: 500,
        group: 'data',
        description: 'Field API names, separated by commas, in the order shown. Empty: every field a visitor can fill in.',
      },
      { kind: 'text', name: 'submitLabel', label: 'Button', maxLength: 60, group: 'content', description: 'Empty: “Save”, or “Save changes” when editing.' },
      { kind: 'text', name: 'successMessage', label: 'After saving', maxLength: 200, group: 'content', description: 'What the visitor reads once it saved.' },
      {
        kind: 'text',
        name: 'afterSave',
        label: 'Then open',
        maxLength: 200,
        group: 'behaviour',
        description: 'A page to go to once saved, with :id for the record — e.g. “/deals/:id”. Empty: stay on the form.',
      },
    ],
  },
];

/**
 * A stored dateTime (UTC, `…Z`) as the visitor's wall clock, the only form `datetime-local`
 * takes. Slicing the stored text showed UTC as if it were local, and since a save converts the
 * input back from local time, saving a record unchanged moved it by the visitor's offset.
 * Mirrors `toDateTimeLocal()` in @dcms/ui, which a site cannot import.
 */
function localInput(value: unknown): string {
  if (typeof value !== 'string' || value === '') return '';
  const at = new Date(value);
  if (Number.isNaN(at.getTime())) return '';
  return new Date(at.getTime() - at.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
}
