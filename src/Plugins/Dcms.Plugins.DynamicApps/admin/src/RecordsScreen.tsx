import { useEffect, useMemo, useState } from 'react';
import { ChevronLeft, ChevronRight, Plus, Trash2 } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Button,
  CenteredSpinner,
  Checkbox,
  DataTable,
  EmptyState,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Sheet,
  SheetBody,
  SheetContent,
  SheetFooter,
  SheetHeader,
  SheetTitle,
  Textarea,
  toast,
  toastApiError,
  type Column,
} from '@dcms/ui';
import {
  instancePath,
  useCan,
  usePluginAiContext,
  usePluginApi,
  usePluginT,
  type PluginScreenProps,
} from '@dcms/plugin-ui';
import { ConfirmDialog } from './ConfirmDialog';
import {
  rootKey,
  usePublished,
  useRecords,
  type AppConfig,
  type FieldDef,
  type RecordPage,
  type RecordRow,
  type RelationshipDef,
  type TableDef,
} from './api';
import { SelectField } from './model/dialogs';

const PAGE_SIZE = 25;

/** A field or a lookup of the table: what a record holds, by api name. */
interface Member {
  id: string;
  apiName: string;
  label: string;
  field?: FieldDef;
  lookup?: RelationshipDef;
}

function membersOf(config: AppConfig, table: TableDef): Member[] {
  return [
    ...table.fields.map((f) => ({ id: f.id, apiName: f.apiName, label: f.displayName, field: f })),
    ...config.relationships
      .filter((r) => r.sourceTableId === table.id && r.kind !== 'manyToMany')
      .map((r) => ({ id: r.id, apiName: r.apiName, label: r.displayName ?? r.apiName, lookup: r })),
  ];
}

/** The app's records (instance screen "records"), as the published model defines them. */
export function RecordsScreen({ instance }: PluginScreenProps) {
  const { t, i18n } = usePluginT();
  const slug = instance!.slug;
  const published = usePublished(slug);
  const config = published.data?.config ?? null;
  const mayWrite = useCan('data-write');
  const [tableId, setTableId] = useState<string>('');
  const [viewId, setViewId] = useState<string>('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<RecordRow | 'new' | null>(null);

  const tables = config?.tables.filter((x) => x.enabled) ?? [];
  const table = tables.find((x) => x.id === tableId) ?? tables[0] ?? null;
  const views = config?.views.filter((v) => v.tableId === table?.id) ?? [];
  const view = views.find((v) => v.id === viewId) ?? views.find((v) => v.isDefault) ?? null;
  useEffect(() => setPage(1), [table?.id, view?.id, search]);

  usePluginAiContext(useMemo(() => ({
    area: 'data-platform',
    summary: `the records of the ${instance!.name} app (Dynamic Apps instance "${slug}")${table ? `, table ${table.apiName}` : ''}`,
    selection: [instance!.id, ...(table ? [table.id] : []), ...(editing && editing !== 'new' ? [editing.id] : [])],
  }), [instance, slug, table, editing]));

  const members = useMemo(() => (config && table ? membersOf(config, table) : []), [config, table]);
  const shown = useMemo(() => {
    if (view && view.columns.length > 0) return view.columns.map((id) => members.find((m) => m.id === id)).filter((m): m is Member => !!m);
    return members.slice(0, 6);
  }, [view, members]);
  const sortBy = view?.sort[0];
  const sortMember = sortBy ? members.find((m) => m.id === sortBy.fieldId) : undefined;

  const records = useRecords(slug, table?.apiName ?? null, {
    search: search.trim() || undefined,
    page,
    pageSize: PAGE_SIZE,
    sort: sortMember ? [{ field: sortMember.apiName, direction: sortBy!.descending ? 'desc' : 'asc' }] : undefined,
  });

  const columns = useMemo<Column<RecordRow>[]>(() => shown.map((m, i) => ({
    id: m.apiName,
    header: m.label,
    primary: i === 0,
    cell: (row) => <span className="line-clamp-1">{display(row[m.apiName], m, i18n.language)}</span>,
  })), [shown, i18n.language]);

  if (published.isLoading) return <CenteredSpinner />;
  if (!config || tables.length === 0) {
    return <EmptyState title={t('records.emptyTitle')} description={t('records.emptyHint')} />;
  }

  const total = records.data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const searchable = table!.fields.some((f) => f.searchable) || table!.primaryFieldId !== undefined;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-3">
        <div className="w-56">
          <SelectField id="records-table" label={t('records.table')} value={table!.id} onChange={(v) => { setTableId(v); setViewId(''); }}
            options={tables.map((x) => ({ value: x.id, label: x.pluralName ?? x.displayName }))} />
        </div>
        {views.length > 0 ? (
          <div className="w-48">
            <SelectField id="records-view" label={t('records.view')} value={view?.id ?? '*'} onChange={(v) => setViewId(v === '*' ? '' : v)}
              options={[{ value: '*', label: t('records.allFields') }, ...views.map((v) => ({ value: v.id, label: v.displayName }))]} />
          </div>
        ) : null}
        {searchable ? (
          <div className="w-64 space-y-1">
            <Label htmlFor="records-search">{t('records.search')}</Label>
            <Input id="records-search" value={search} onChange={(e) => setSearch(e.target.value)} />
          </div>
        ) : null}
        {mayWrite ? (
          <Button className="ml-auto" onClick={() => setEditing('new')}><Plus className="size-4" /> {t('records.new')}</Button>
        ) : null}
      </div>

      <DataTable rows={records.data?.items ?? []} columns={columns} rowKey={(r) => r.id} isLoading={records.isLoading}
        empty={t('records.none')} onRowClick={(row) => setEditing(row)} />

      <div className="flex items-center justify-end gap-2 text-sm">
        <span className="text-muted-foreground">{t('records.count', { count: total })}</span>
        <Button variant="outline" size="icon" aria-label={t('records.previous')} disabled={page <= 1} onClick={() => setPage(page - 1)}>
          <ChevronLeft className="size-4" />
        </Button>
        <span>{page} / {pages}</span>
        <Button variant="outline" size="icon" aria-label={t('records.next')} disabled={page >= pages} onClick={() => setPage(page + 1)}>
          <ChevronRight className="size-4" />
        </Button>
      </div>

      {editing ? (
        <RecordSheet slug={slug} config={config} table={table!} members={members} record={editing === 'new' ? null : editing}
          editable={mayWrite} onClose={() => setEditing(null)} />
      ) : null}
    </div>
  );
}

/** How a stored value reads in a table cell. */
function display(value: unknown, member: Member, language: string): string {
  if (value === null || value === undefined) return '';
  if (member.lookup) return typeof value === 'object' ? String((value as Record<string, unknown>).id ?? '') : String(value);
  switch (member.field?.type) {
    case 'boolean':
      return value ? '✓' : '—';
    case 'dateTime':
      return new Date(String(value)).toLocaleString(language);
    case 'multiChoice':
      return Array.isArray(value) ? value.join(', ') : String(value);
    case 'json':
      return JSON.stringify(value);
    default:
      return String(value);
  }
}

/** One record, created or edited: an input per field type; the server validates and says what is wrong where. */
function RecordSheet({ slug, config, table, members, record, editable, onClose }: {
  slug: string;
  config: AppConfig;
  table: TableDef;
  members: Member[];
  record: RecordRow | null;
  editable: boolean;
  onClose: () => void;
}) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const qc = useQueryClient();
  const mayDelete = useCan('data-delete');
  const [values, setValues] = useState<Record<string, unknown>>(() => ({ ...(record ?? {}) }));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [confirming, setConfirming] = useState(false);

  const save = useMutation({
    mutationFn: async () => {
      const body: Record<string, unknown> = {};
      for (const m of members) {
        if (m.field?.readOnly) continue;
        const now = values[m.apiName];
        const was = record?.[m.apiName];
        if (record === null ? now !== undefined && now !== '' : JSON.stringify(now ?? null) !== JSON.stringify(was ?? null)) {
          body[m.apiName] = now === '' ? null : now;
        }
      }
      if (record) {
        body.version = record.version;
        return api.patch(instancePath(slug, `/_records/${table.apiName}/${record.id}`), body);
      }
      return api.post(instancePath(slug, `/_records/${table.apiName}`), body);
    },
    onSuccess: async () => {
      toast.success(t('records.saved'));
      await qc.invalidateQueries({ queryKey: [rootKey(slug), 'records'] });
      onClose();
    },
    onError: (error) => {
      const fields = (error as { detail?: { fields?: Record<string, string> } }).detail?.fields;
      if (fields) setErrors(fields);
      else if ((error as { status?: number }).status === 409) toast.error(t('records.conflict'));
      else toastApiError(error, t);
    },
  });

  const remove = useMutation({
    mutationFn: () => api.del(instancePath(slug, `/_records/${table.apiName}/${record!.id}?version=${record!.version}`)),
    onSuccess: async () => {
      toast.success(t('records.deleted'));
      await qc.invalidateQueries({ queryKey: [rootKey(slug), 'records'] });
      onClose();
    },
    onError: (error) => toastApiError(error, t),
  });

  return (
    <Sheet open onOpenChange={(open) => !open && onClose()}>
      <SheetContent className="w-full max-w-xl sm:max-w-xl">
        <SheetHeader>
          <SheetTitle>{record ? t('records.edit', { table: table.displayName }) : t('records.create', { table: table.displayName })}</SheetTitle>
        </SheetHeader>
        <SheetBody className="space-y-3 overflow-y-auto">
          {members.map((m) => (
            <MemberInput key={m.id} slug={slug} config={config} member={m} value={values[m.apiName]}
              disabled={!editable || m.field?.readOnly === true}
              error={errors[m.apiName]}
              onChange={(value) => setValues((v) => ({ ...v, [m.apiName]: value }))} />
          ))}
          {errors._record ? <p className="text-sm text-destructive">{errors._record}</p> : null}
        </SheetBody>
        <SheetFooter>
          {record && mayDelete ? (
            <Button variant="outline" className="mr-auto" onClick={() => setConfirming(true)}><Trash2 className="size-4" /> {t('actions.delete')}</Button>
          ) : null}
          <Button variant="outline" onClick={onClose}>{t('actions.cancel')}</Button>
          {editable ? <Button disabled={save.isPending} onClick={() => save.mutate()}>{t('actions.save')}</Button> : null}
        </SheetFooter>
        <ConfirmDialog open={confirming} onOpenChange={setConfirming} title={t('records.deleteTitle')}
          description={t('records.deleteHint')} confirmLabel={t('actions.delete')} pending={remove.isPending}
          onConfirm={() => remove.mutate()} />
      </SheetContent>
    </Sheet>
  );
}

function MemberInput({ slug, config, member, value, disabled, error, onChange }: {
  slug: string;
  config: AppConfig;
  member: Member;
  value: unknown;
  disabled: boolean;
  error?: string;
  onChange: (value: unknown) => void;
}) {
  const { t } = usePluginT();
  const id = `rec-${member.apiName}`;
  const field = member.field;
  const choices = config.choiceSets.find((c) => c.id === field?.choiceSetId)?.options ?? [];
  const text = value === null || value === undefined ? '' : String(value);

  let input: React.ReactNode;
  if (member.lookup) {
    input = <LookupInput slug={slug} config={config} relationship={member.lookup} id={id} value={text} disabled={disabled} onChange={onChange} />;
  } else {
    switch (field!.type) {
      case 'longText':
        input = <Textarea id={id} value={text} disabled={disabled} onChange={(e) => onChange(e.target.value)} rows={4} />;
        break;
      case 'integer':
      case 'decimal':
        input = <Input id={id} type="number" value={text} disabled={disabled}
          onChange={(e) => onChange(e.target.value === '' ? '' : Number(e.target.value))} />;
        break;
      case 'boolean':
        input = <Checkbox id={id} checked={value === true} disabled={disabled} onCheckedChange={(v) => onChange(v === true)} />;
        break;
      case 'date':
        input = <Input id={id} type="date" value={text} disabled={disabled} onChange={(e) => onChange(e.target.value)} />;
        break;
      case 'dateTime':
        input = <Input id={id} type="datetime-local" value={text ? toLocalInput(text) : ''} disabled={disabled}
          onChange={(e) => onChange(e.target.value ? new Date(e.target.value).toISOString() : '')} />;
        break;
      case 'choice':
        input = (
          <Select value={text || undefined} disabled={disabled} onValueChange={onChange}>
            <SelectTrigger id={id}><SelectValue /></SelectTrigger>
            <SelectContent>{choices.map((c) => <SelectItem key={c.value} value={c.value}>{c.label}</SelectItem>)}</SelectContent>
          </Select>
        );
        break;
      case 'multiChoice': {
        const selected = Array.isArray(value) ? (value as string[]) : [];
        input = (
          <div className="flex flex-wrap gap-3">
            {choices.map((c) => (
              <label key={c.value} className="flex items-center gap-1 text-sm">
                <Checkbox checked={selected.includes(c.value)} disabled={disabled}
                  onCheckedChange={(on) => onChange(on === true ? [...selected, c.value] : selected.filter((x) => x !== c.value))} />
                {c.label}
              </label>
            ))}
          </div>
        );
        break;
      }
      case 'json':
        input = <Textarea id={id} className="font-mono text-xs" disabled={disabled} rows={4}
          defaultValue={value === undefined ? '' : JSON.stringify(value, null, 2)}
          onChange={(e) => { try { onChange(e.target.value ? JSON.parse(e.target.value) : ''); } catch { /* kept until it parses */ } }} />;
        break;
      default:
        input = <Input id={id} type={field!.type === 'email' ? 'email' : field!.type === 'url' ? 'url' : 'text'} value={text}
          disabled={disabled} onChange={(e) => onChange(e.target.value)} />;
    }
  }

  return (
    <div className="space-y-1">
      <Label htmlFor={id}>
        {member.label}
        {field?.required || member.lookup?.required ? <span className="text-destructive"> *</span> : null}
        {field?.readOnly ? <span className="ml-1 text-xs text-muted-foreground">({t('flags.readOnly')})</span> : null}
      </Label>
      {input}
      {error ? <p className="text-xs text-destructive">{error}</p> : null}
    </div>
  );
}

/** A lookup as a choice among the target table's records, named by its primary field. */
function LookupInput({ slug, config, relationship, id, value, disabled, onChange }: {
  slug: string;
  config: AppConfig;
  relationship: RelationshipDef;
  id: string;
  value: string;
  disabled: boolean;
  onChange: (value: unknown) => void;
}) {
  const api = usePluginApi();
  const target = config.tables.find((x) => x.id === relationship.targetTableId);
  const primary = target?.fields.find((f) => f.id === target.primaryFieldId)?.apiName;
  const options = useQuery({
    queryKey: [rootKey(slug), 'records', target?.apiName, 'options'],
    queryFn: () => api.post<RecordPage>(instancePath(slug, `/_records/${target!.apiName}/query`), { pageSize: 200 }),
    enabled: target !== undefined,
  });
  return (
    <Select value={value || undefined} disabled={disabled} onValueChange={onChange}>
      <SelectTrigger id={id}><SelectValue /></SelectTrigger>
      <SelectContent>
        {(options.data?.items ?? []).map((r) => (
          <SelectItem key={r.id} value={r.id}>{primary && r[primary] ? String(r[primary]) : r.id}</SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

function toLocalInput(iso: string): string {
  const date = new Date(iso);
  const offset = date.getTimezoneOffset() * 60_000;
  return new Date(date.getTime() - offset).toISOString().slice(0, 16);
}
