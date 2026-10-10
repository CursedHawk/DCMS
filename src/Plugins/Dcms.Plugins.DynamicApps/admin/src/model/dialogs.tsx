import { useState, type ReactNode } from 'react';
import {
  Button,
  Checkbox,
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Textarea,
} from '@dcms/ui';
import { usePluginT } from '@dcms/plugin-ui';
import {
  API_NAME,
  FIELD_TYPES,
  apiNameOf,
  type ChangeOperation,
  type ChoiceSetDef,
  type FieldDef,
  type FieldType,
  type IndexDef,
  type RelationshipDef,
  type TableDef,
  type ViewDef,
} from '../api';

// ---------------------------------------------------------------------------------------------
// A small form kit shared by the editors
// ---------------------------------------------------------------------------------------------

export function FormDialog({ title, pending, canSave = true, onCancel, onSave, children, wide }: {
  title: string;
  pending: boolean;
  canSave?: boolean;
  onCancel: () => void;
  onSave: () => void;
  children: ReactNode;
  wide?: boolean;
}) {
  const { t } = usePluginT();
  return (
    <Dialog open onOpenChange={(open) => !open && onCancel()}>
      <DialogContent className={wide ? 'max-w-3xl' : 'max-w-lg'}>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
        </DialogHeader>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            if (canSave && !pending) onSave();
          }}
        >
          <DialogBody className="max-h-[65vh] space-y-3 overflow-y-auto">{children}</DialogBody>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onCancel}>{t('actions.cancel')}</Button>
            <Button type="submit" disabled={!canSave || pending}>{t('actions.save')}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export function TextField({ id, label, value, onChange, hint, mono, multiline, invalid, type = 'text' }: {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  hint?: string;
  mono?: boolean;
  multiline?: boolean;
  invalid?: boolean;
  type?: string;
}) {
  return (
    <div className="space-y-1">
      <Label htmlFor={id}>{label}</Label>
      {multiline ? (
        <Textarea id={id} value={value} onChange={(e) => onChange(e.target.value)} className={mono ? 'font-mono text-xs' : undefined}
          rows={6} aria-invalid={invalid || undefined} />
      ) : (
        <Input id={id} type={type} value={value} onChange={(e) => onChange(e.target.value)} className={mono ? 'font-mono' : undefined}
          aria-invalid={invalid || undefined} />
      )}
      {hint ? <p className="text-xs text-muted-foreground">{hint}</p> : null}
    </div>
  );
}

export function CheckField({ id, label, checked, onChange, hint }: {
  id: string;
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  hint?: string;
}) {
  return (
    <div className="flex items-start gap-2">
      <Checkbox id={id} checked={checked} onCheckedChange={(v) => onChange(v === true)} className="mt-0.5" />
      <div>
        <Label htmlFor={id} className="font-normal">{label}</Label>
        {hint ? <p className="text-xs text-muted-foreground">{hint}</p> : null}
      </div>
    </div>
  );
}

const NO_COPY = '-';

/** A new field or lookup that replaces a live one: whose values it takes over when published. */
function CopyFromField({ id, value, onChange, fields }: { id: string; value: string; onChange: (v: string) => void; fields: FieldDef[] }) {
  const { t } = usePluginT();
  return (
    <div className="space-y-1">
      <SelectField id={id} label={t('form.copyFrom')} value={value} onChange={onChange}
        options={[{ value: NO_COPY, label: t('form.copyNone') }, ...fields.map((f) => ({ value: f.apiName, label: `${f.displayName} (${f.apiName})` }))]} />
      <p className="text-xs text-muted-foreground">{t('form.copyFromHint')}</p>
    </div>
  );
}

export function SelectField<T extends string>({ id, label, value, onChange, options, placeholder }: {
  id: string;
  label: string;
  value: T | '';
  onChange: (value: T) => void;
  options: { value: T; label: string }[];
  placeholder?: string;
}) {
  return (
    <div className="space-y-1">
      <Label htmlFor={id}>{label}</Label>
      <Select value={value || undefined} onValueChange={(v) => onChange(v as T)}>
        <SelectTrigger id={id}><SelectValue placeholder={placeholder} /></SelectTrigger>
        <SelectContent>
          {options.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}
        </SelectContent>
      </Select>
    </div>
  );
}

/** Display name and api name together: the api name follows the label until edited by hand. */
function useNames(initialLabel: string, initialApi: string) {
  const [label, setLabel] = useState(initialLabel);
  const [api, setApi] = useState(initialApi);
  const [touched, setTouched] = useState(initialApi.length > 0);
  return {
    label,
    api,
    setLabel: (value: string) => {
      setLabel(value);
      if (!touched) setApi(apiNameOf(value));
    },
    setApi: (value: string) => {
      setTouched(true);
      setApi(value);
    },
    valid: label.trim().length > 0 && API_NAME.test(api),
  };
}

function Names({ names, idPrefix }: { names: ReturnType<typeof useNames>; idPrefix: string }) {
  const { t } = usePluginT();
  return (
    <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
      <TextField id={`${idPrefix}-label`} label={t('form.displayName')} value={names.label} onChange={names.setLabel} />
      <TextField id={`${idPrefix}-api`} label={t('form.apiName')} value={names.api} onChange={names.setApi} mono
        invalid={names.api.length > 0 && !API_NAME.test(names.api)} hint={t('form.apiNameHint')} />
    </div>
  );
}

/** Only what changed, as a merge patch: unchanged keys are left out, cleared ones sent as null. */
function patchOf<T extends object>(before: T, after: Record<string, unknown>): Record<string, unknown> {
  const patch: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(after)) {
    const old = (before as Record<string, unknown>)[key];
    if (JSON.stringify(old ?? null) !== JSON.stringify(value ?? null)) patch[key] = value ?? null;
  }
  return patch;
}

const optional = (text: string) => (text.trim() ? text.trim() : undefined);
const number = (text: string) => (text.trim() === '' || Number.isNaN(Number(text)) ? undefined : Number(text));

// ---------------------------------------------------------------------------------------------
// Editors
// ---------------------------------------------------------------------------------------------

type Save = (operations: ChangeOperation[]) => void;

export function TableDialog({ table, pending, onCancel, onSave }: { table?: TableDef; pending: boolean; onCancel: () => void; onSave: Save }) {
  const { t } = usePluginT();
  const names = useNames(table?.displayName ?? '', table?.apiName ?? '');
  const [plural, setPlural] = useState(table?.pluralName ?? '');
  const [description, setDescription] = useState(table?.description ?? '');
  const [enabled, setEnabled] = useState(table?.enabled ?? true);

  const save = () => {
    const value = { apiName: names.api, displayName: names.label.trim(), pluralName: optional(plural), description: optional(description), enabled };
    onSave(table
      ? [{ op: 'update', type: 'table', target: table.id, value: patchOf(table, value) }]
      // A new table starts with the field that names its records.
      : [{ op: 'create', type: 'table', value: { ...value, primaryFieldId: 'name', fields: [{ apiName: 'name', displayName: t('form.nameField'), type: 'text', required: true, searchable: true, sortable: true }] } }]);
  };

  return (
    <FormDialog title={t(table ? 'model.editTable' : 'model.newTable')} pending={pending} canSave={names.valid} onCancel={onCancel} onSave={save}>
      <Names names={names} idPrefix="table" />
      <TextField id="table-plural" label={t('form.pluralName')} value={plural} onChange={setPlural} />
      <TextField id="table-description" label={t('form.description')} value={description} onChange={setDescription} />
      {table ? <CheckField id="table-enabled" label={t('form.enabled')} checked={enabled} onChange={setEnabled} hint={t('form.enabledHint')} /> : null}
    </FormDialog>
  );
}

const TEXT_TYPES: FieldType[] = ['text', 'longText', 'email', 'url'];
const NUMBER_TYPES: FieldType[] = ['integer', 'decimal'];

export function FieldDialog({ table, field, choiceSets, liveTables = [], pending, onCancel, onSave }: {
  table: TableDef;
  field?: FieldDef;
  choiceSets: ChoiceSetDef[];
  liveTables?: TableDef[];
  pending: boolean;
  onCancel: () => void;
  onSave: Save;
}) {
  const { t } = usePluginT();
  const names = useNames(field?.displayName ?? '', field?.apiName ?? '');
  const [type, setType] = useState<FieldType>(field?.type ?? 'text');
  const [choiceSetId, setChoiceSetId] = useState(field?.choiceSetId ?? '');
  const [description, setDescription] = useState(field?.description ?? '');
  const [flags, setFlags] = useState({
    required: field?.required ?? false,
    unique: field?.unique ?? false,
    searchable: field?.searchable ?? false,
    sortable: field?.sortable ?? false,
    filterable: field?.filterable ?? false,
    readOnly: field?.readOnly ?? false,
    hiddenFromPublic: field?.hiddenFromPublic ?? false,
    deprecated: field?.deprecated ?? false,
  });
  const [maxLength, setMaxLength] = useState(field?.maxLength?.toString() ?? '');
  const [minimum, setMinimum] = useState(field?.minimum?.toString() ?? '');
  const [maximum, setMaximum] = useState(field?.maximum?.toString() ?? '');
  const [defaultText, setDefaultText] = useState(field?.default === undefined ? '' : typeof field.default === 'string' ? field.default : JSON.stringify(field.default));
  const [primary, setPrimary] = useState(field !== undefined && field.id === table.primaryFieldId);
  const isChoice = type === 'choice' || type === 'multiChoice';
  const [copyFrom, setCopyFrom] = useState(NO_COPY);
  const copyable = field ? [] : liveTables.find((x) => x.id === table.id)?.fields ?? [];

  const defaultValue = (): unknown => {
    if (!defaultText.trim()) return undefined;
    if (NUMBER_TYPES.includes(type)) return number(defaultText);
    if (type === 'boolean') return defaultText.trim() === 'true';
    if (type === 'multiChoice' || type === 'json') {
      try {
        return JSON.parse(defaultText);
      } catch {
        return defaultText;
      }
    }
    return defaultText;
  };

  const save = () => {
    const value: Record<string, unknown> = {
      apiName: names.api,
      displayName: names.label.trim(),
      description: optional(description),
      type,
      ...flags,
      choiceSetId: isChoice ? choiceSetId || undefined : undefined,
      maxLength: TEXT_TYPES.includes(type) ? number(maxLength) : undefined,
      minimum: NUMBER_TYPES.includes(type) ? number(minimum) : undefined,
      maximum: NUMBER_TYPES.includes(type) ? number(maximum) : undefined,
      default: defaultValue(),
      copyFrom: field || copyFrom === NO_COPY ? undefined : copyFrom,
    };
    const ops: ChangeOperation[] = field
      ? [{ op: 'update', type: 'field', target: field.id, value: patchOf(field, value) }]
      : [{ op: 'create', type: 'field', target: table.id, value: Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined)) }];
    const wasPrimary = field !== undefined && field.id === table.primaryFieldId;
    if (primary !== wasPrimary) {
      ops.push({ op: 'update', type: 'table', target: table.id, value: { primaryFieldId: primary ? (field?.id ?? names.api) : null } });
    }
    onSave(ops);
  };

  const flag = (key: keyof typeof flags) => (checked: boolean) => setFlags((f) => ({ ...f, [key]: checked }));

  return (
    <FormDialog title={t(field ? 'model.editField' : 'model.newField')} pending={pending}
      canSave={names.valid && (!isChoice || choiceSetId !== '')} onCancel={onCancel} onSave={save} wide>
      <Names names={names} idPrefix="field" />
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <SelectField id="field-type" label={t('fields.type')} value={type} onChange={setType}
          options={FIELD_TYPES.map((value) => ({ value, label: t(`types.${value}`) }))} />
        {isChoice ? (
          <SelectField id="field-choices" label={t('form.choiceSet')} value={choiceSetId} onChange={setChoiceSetId}
            placeholder={choiceSets.length === 0 ? t('form.noChoiceSets') : undefined}
            options={choiceSets.map((c) => ({ value: c.id, label: c.displayName }))} />
        ) : null}
      </div>
      <TextField id="field-description" label={t('form.description')} value={description} onChange={setDescription} />
      {copyable.length > 0 ? (
        <CopyFromField id="field-copy-from" value={copyFrom} onChange={setCopyFrom} fields={copyable} />
      ) : null}
      <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
        <CheckField id="f-required" label={t('flags.required')} checked={flags.required} onChange={flag('required')} />
        <CheckField id="f-unique" label={t('flags.unique')} checked={flags.unique} onChange={flag('unique')} />
        <CheckField id="f-primary" label={t('flags.primary')} checked={primary} onChange={setPrimary} hint={t('form.primaryHint')} />
        <CheckField id="f-searchable" label={t('flags.searchable')} checked={flags.searchable} onChange={flag('searchable')} />
        <CheckField id="f-sortable" label={t('flags.sortable')} checked={flags.sortable} onChange={flag('sortable')} />
        <CheckField id="f-filterable" label={t('flags.filterable')} checked={flags.filterable} onChange={flag('filterable')} />
        <CheckField id="f-readonly" label={t('flags.readOnly')} checked={flags.readOnly} onChange={flag('readOnly')} hint={t('form.readOnlyHint')} />
        <CheckField id="f-hidden" label={t('flags.hidden')} checked={flags.hiddenFromPublic} onChange={flag('hiddenFromPublic')} hint={t('form.hiddenHint')} />
        <CheckField id="f-deprecated" label={t('flags.deprecated')} checked={flags.deprecated} onChange={flag('deprecated')} />
      </div>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
        {TEXT_TYPES.includes(type) ? (
          <TextField id="field-max" label={t('form.maxLength')} value={maxLength} onChange={setMaxLength} type="number" />
        ) : null}
        {NUMBER_TYPES.includes(type) ? (
          <>
            <TextField id="field-min" label={t('form.minimum')} value={minimum} onChange={setMinimum} type="number" />
            <TextField id="field-maxv" label={t('form.maximum')} value={maximum} onChange={setMaximum} type="number" />
          </>
        ) : null}
        <TextField id="field-default" label={t('form.default')} value={defaultText} onChange={setDefaultText} />
      </div>
    </FormDialog>
  );
}

export function IndexDialog({ table, index, pending, onCancel, onSave }: { table: TableDef; index?: IndexDef; pending: boolean; onCancel: () => void; onSave: Save }) {
  const { t } = usePluginT();
  const [api, setApi] = useState(index?.apiName ?? '');
  const [fieldIds, setFieldIds] = useState<string[]>(index?.fieldIds ?? []);
  const [unique, setUnique] = useState(index?.unique ?? false);
  const toggle = (id: string) => (checked: boolean) => setFieldIds((ids) => (checked ? [...ids, id] : ids.filter((x) => x !== id)));
  const save = () => {
    const value = { apiName: api, fieldIds, unique };
    onSave(index
      ? [{ op: 'update', type: 'index', target: index.id, value: patchOf(index, value) }]
      : [{ op: 'create', type: 'index', target: table.id, value }]);
  };
  return (
    <FormDialog title={t(index ? 'model.editIndex' : 'model.newIndex')} pending={pending}
      canSave={API_NAME.test(api) && fieldIds.length > 0 && fieldIds.length <= 5} onCancel={onCancel} onSave={save}>
      <TextField id="index-api" label={t('form.apiName')} value={api} onChange={setApi} mono />
      <fieldset className="space-y-1">
        <legend className="text-sm font-medium">{t('form.indexFields')}</legend>
        {table.fields.map((f) => (
          <CheckField key={f.id} id={`ix-${f.id}`} label={`${f.displayName} (${f.apiName})`} checked={fieldIds.includes(f.id)} onChange={toggle(f.id)} />
        ))}
      </fieldset>
      <CheckField id="index-unique" label={t('flags.unique')} checked={unique} onChange={setUnique} hint={t('form.uniqueIndexHint')} />
    </FormDialog>
  );
}

export function ViewDialog({ table, view, relationships, pending, onCancel, onSave }: {
  table: TableDef;
  view?: ViewDef;
  relationships: RelationshipDef[];
  pending: boolean;
  onCancel: () => void;
  onSave: Save;
}) {
  const { t } = usePluginT();
  const names = useNames(view?.displayName ?? '', view?.apiName ?? '');
  const members = [
    ...table.fields.map((f) => ({ id: f.id, label: f.displayName, sortable: f.type !== 'multiChoice' && f.type !== 'json' })),
    ...relationships.filter((r) => r.sourceTableId === table.id && r.kind !== 'manyToMany')
      .map((r) => ({ id: r.id, label: r.displayName ?? r.apiName, sortable: false })),
  ];
  const [columns, setColumns] = useState<string[]>(view?.columns ?? []);
  const [sortField, setSortField] = useState(view?.sort[0]?.fieldId ?? '');
  const [descending, setDescending] = useState(view?.sort[0]?.descending ?? false);
  const [isDefault, setIsDefault] = useState(view?.isDefault ?? false);
  const toggle = (id: string) => (checked: boolean) => setColumns((ids) => (checked ? [...ids, id] : ids.filter((x) => x !== id)));
  const save = () => {
    const value = {
      apiName: names.api,
      displayName: names.label.trim(),
      tableId: table.id,
      columns,
      sort: sortField ? [{ fieldId: sortField, descending }] : [],
      isDefault,
    };
    onSave(view
      ? [{ op: 'update', type: 'view', target: view.id, value: patchOf(view, value) }]
      : [{ op: 'create', type: 'view', value }]);
  };
  return (
    <FormDialog title={t(view ? 'model.editView' : 'model.newView')} pending={pending} canSave={names.valid} onCancel={onCancel} onSave={save}>
      <Names names={names} idPrefix="view" />
      <fieldset className="space-y-1">
        <legend className="text-sm font-medium">{t('form.columns')}</legend>
        {members.map((m) => <CheckField key={m.id} id={`col-${m.id}`} label={m.label} checked={columns.includes(m.id)} onChange={toggle(m.id)} />)}
      </fieldset>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <SelectField id="view-sort" label={t('form.sortBy')} value={sortField} onChange={setSortField}
          options={members.filter((m) => m.sortable).map((m) => ({ value: m.id, label: m.label }))} />
        <div className="flex items-end pb-2">
          <CheckField id="view-desc" label={t('form.descending')} checked={descending} onChange={setDescending} />
        </div>
      </div>
      <CheckField id="view-default" label={t('model.defaultView')} checked={isDefault} onChange={setIsDefault} />
    </FormDialog>
  );
}

export function RelationshipDialog({ relationship, tables, liveTables = [], pending, onCancel, onSave }: {
  relationship?: RelationshipDef;
  tables: TableDef[];
  liveTables?: TableDef[];
  pending: boolean;
  onCancel: () => void;
  onSave: Save;
}) {
  const { t } = usePluginT();
  const [kind, setKind] = useState<RelationshipDef['kind']>(relationship?.kind ?? 'manyToOne');
  const [source, setSource] = useState(relationship?.sourceTableId ?? '');
  const [target, setTarget] = useState(relationship?.targetTableId ?? '');
  const names = useNames(relationship?.displayName ?? '', relationship?.apiName ?? '');
  const [inverse, setInverse] = useState(relationship?.inverseApiName ?? '');
  const [required, setRequired] = useState(relationship?.required ?? false);
  const [onDelete, setOnDelete] = useState<RelationshipDef['onDelete']>(relationship?.onDelete ?? 'restrict');
  const [copyFrom, setCopyFrom] = useState(NO_COPY);
  // Record ids kept as text: what a lookup can take over.
  const copyable = relationship || kind === 'manyToMany' ? []
    : (liveTables.find((x) => x.id === source)?.fields ?? []).filter((f) => f.type === 'text' || f.type === 'longText');
  const tableOptions = tables.map((x) => ({ value: x.id, label: `${x.displayName} (${x.apiName})` }));
  const save = () => {
    const value = {
      apiName: names.api,
      displayName: optional(names.label),
      kind,
      sourceTableId: source,
      targetTableId: target,
      inverseApiName: optional(inverse),
      required: kind === 'manyToMany' ? false : required,
      onDelete,
      copyFrom: copyable.length === 0 || copyFrom === NO_COPY ? undefined : copyFrom,
    };
    onSave(relationship
      ? [{ op: 'update', type: 'relationship', target: relationship.id, value: patchOf(relationship, value) }]
      : [{ op: 'create', type: 'relationship', value }]);
  };
  const canSave = API_NAME.test(names.api) && source !== '' && target !== ''
    && (kind !== 'manyToMany' || API_NAME.test(inverse)) && (inverse === '' || API_NAME.test(inverse));
  return (
    <FormDialog title={t(relationship ? 'model.editRelationship' : 'model.newRelationship')} pending={pending} canSave={canSave} onCancel={onCancel} onSave={save}>
      <SelectField id="rel-kind" label={t('form.kind')} value={kind} onChange={setKind}
        options={(['manyToOne', 'oneToOne', 'manyToMany'] as const).map((value) => ({ value, label: t(`kinds.rel.${value}`) }))} />
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <SelectField id="rel-source" label={t('form.source')} value={source} onChange={setSource} options={tableOptions} />
        <SelectField id="rel-target" label={t('form.target')} value={target} onChange={setTarget} options={tableOptions} />
      </div>
      <Names names={names} idPrefix="rel" />
      <TextField id="rel-inverse" label={t('form.inverseName')} value={inverse} onChange={setInverse} mono hint={t('form.inverseHint')} />
      {copyable.length > 0 ? (
        <CopyFromField id="rel-copy-from" value={copyFrom} onChange={setCopyFrom} fields={copyable} />
      ) : null}
      {kind !== 'manyToMany' ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <CheckField id="rel-required" label={t('flags.required')} checked={required} onChange={setRequired} />
          <SelectField id="rel-ondelete" label={t('form.onDelete')} value={onDelete} onChange={setOnDelete}
            options={(['restrict', 'setNull', 'cascade'] as const).map((value) => ({ value, label: t(`onDelete.${value}`) }))} />
        </div>
      ) : null}
    </FormDialog>
  );
}

export function ChoiceSetDialog({ set, pending, onCancel, onSave }: { set?: ChoiceSetDef; pending: boolean; onCancel: () => void; onSave: Save }) {
  const { t } = usePluginT();
  const names = useNames(set?.displayName ?? '', set?.apiName ?? '');
  const [options, setOptions] = useState(set?.options.map((o) => (o.label === o.value ? o.value : `${o.value} | ${o.label}`)).join('\n') ?? '');
  const parsed = options.split('\n').map((line) => line.trim()).filter(Boolean).map((line) => {
    const [value, label] = line.split('|').map((part) => part.trim());
    return { value: value!, label: label || value! };
  });
  const save = () => {
    const value = { apiName: names.api, displayName: names.label.trim(), options: parsed };
    onSave(set
      ? [{ op: 'update', type: 'choiceSet', target: set.id, value: patchOf(set, value) }]
      : [{ op: 'create', type: 'choiceSet', value }]);
  };
  return (
    <FormDialog title={t(set ? 'model.editChoiceSet' : 'model.newChoiceSet')} pending={pending}
      canSave={names.valid && parsed.length > 0} onCancel={onCancel} onSave={save}>
      <Names names={names} idPrefix="choices" />
      <TextField id="choices-options" label={t('form.options')} value={options} onChange={setOptions} multiline mono hint={t('form.optionsHint')} />
    </FormDialog>
  );
}
