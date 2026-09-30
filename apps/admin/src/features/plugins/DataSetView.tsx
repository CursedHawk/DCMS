import { useDeferredValue, useEffect, useMemo, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Check, ChevronLeft, ChevronRight, Download, Minus, Plus, Trash2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import type { RJSFSchema } from '@rjsf/utils';
import {
  Badge,
  Button,
  CenteredSpinner,
  DataTable,
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  EmptyState,
  FilterBar,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Sheet,
  SheetBody,
  SheetContent,
  SheetDescription,
  SheetFooter,
  SheetHeader,
  SheetTitle,
  toastApiError,
  type Column,
} from '@dcms/ui';
import { AuthedImage } from '../../components/AuthedImage';
import { SchemaForm } from '../../components/SchemaForm';
import { configWidgets } from './configWidgets';
import {
  dataApi,
  useDataPage,
  useDataRow,
  type DataAction,
  type DataColumn,
  type DataQuery,
  type DataRow,
  type DataSet,
} from './dataApi';

const PAGE_SIZE = 25;
const ALL = '__all';

/**
 * Any plugin's data set, rendered from the schema the plugin describes: filters and search
 * above, a server-paged and server-sorted table, and a sheet to read, edit, act on or delete
 * one row. Nothing here knows which plugin it is showing, which is the point — an installed
 * plugin's data looks and works the same as a built-in one's.
 */
export function DataSetView({ slug, set }: { slug: string; set: DataSet }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const [search, setSearch] = useState('');
  const deferredSearch = useDeferredValue(search);
  const [filters, setFilters] = useState<Record<string, string>>({});
  const [sort, setSort] = useState<{ columnId: string; direction: 'asc' | 'desc' } | null>(
    set.defaultSort ? { columnId: set.defaultSort, direction: set.defaultDescending ? 'desc' : 'asc' } : null,
  );
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [openKey, setOpenKey] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [pending, setPending] = useState<{ action: DataAction; keys: string[] } | null>(null);

  // A new query starts from its first page with nothing selected.
  useEffect(() => {
    setPage(1);
    setSelected(new Set());
  }, [deferredSearch, filters, sort]);

  const query: DataQuery = {
    search: deferredSearch,
    sort: sort?.columnId ?? null,
    descending: sort?.direction === 'desc',
    filters,
    page,
    pageSize: PAGE_SIZE,
  };
  const data = useDataPage(slug, set.id, query);
  const refresh = () => qc.invalidateQueries({ queryKey: ['plugin-data'] });

  const bulkActions = set.canWrite ? set.actions.filter((a) => a.bulk) : [];
  const columns = useMemo<Column<DataRow>[]>(
    () =>
      set.columns.map((c) => ({
        id: c.key,
        header: c.label,
        primary: c.primary,
        sortable: c.sortable,
        align: c.kind === 'number' || c.kind === 'bytes' ? 'right' : 'left',
        cell: (row) => <Value column={c} value={row.values[c.key]} />,
      })),
    [set.columns],
  );

  const total = data.data?.total ?? 0;
  const from = total === 0 ? 0 : (page - 1) * PAGE_SIZE + 1;
  const to = Math.min(page * PAGE_SIZE, total);
  const activeFilters = Object.values(filters).filter(Boolean).length;

  return (
    <div>
      <FilterBar
        search={set.searchable ? search : undefined}
        onSearchChange={set.searchable ? setSearch : undefined}
        searchPlaceholder={t('pluginPage.data.search', { title: set.title.toLowerCase() })}
        activeCount={activeFilters}
        onClear={() => setFilters({})}
        clearLabel={t('pluginPage.data.clearFilters')}
        actions={
          <>
            {selected.size > 0
              ? bulkActions.map((a) => (
                  <Button
                    key={a.id}
                    size="sm"
                    variant={a.risk === 'dangerous' ? 'destructive' : 'outline'}
                    onClick={() => setPending({ action: a, keys: [...selected] })}
                  >
                    {a.label} ({selected.size})
                  </Button>
                ))
              : null}
            {set.canCreate ? (
              <Button size="sm" onClick={() => setCreating(true)}>
                <Plus className="h-4 w-4" aria-hidden />
                {t('pluginPage.data.new')}
              </Button>
            ) : null}
          </>
        }
      >
        {set.filters.map((f) => (
          <Select
            key={f.key}
            value={filters[f.key] || ALL}
            onValueChange={(v) => setFilters((cur) => ({ ...cur, [f.key]: v === ALL ? '' : v }))}
          >
            <SelectTrigger className="h-9 w-auto min-w-36" aria-label={f.label}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL}>{t('pluginPage.data.anyFilter', { label: f.label })}</SelectItem>
              {f.options.map((o) => (
                <SelectItem key={o.value} value={o.value}>
                  {o.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        ))}
      </FilterBar>

      <DataTable
        rows={data.data?.rows}
        columns={columns}
        rowKey={(r) => r.key}
        isLoading={data.isLoading}
        error={data.isError ? <EmptyState title={t('pluginPage.data.loadFailed')} /> : undefined}
        empty={
          <EmptyState
            title={activeFilters || deferredSearch ? t('common.noResults') : t('pluginPage.data.empty', { title: set.title })}
          />
        }
        onRowClick={(r) => setOpenKey(r.key)}
        sort={sort}
        onSortChange={setSort}
        selection={bulkActions.length > 0 ? { selected, onChange: setSelected } : undefined}
        caption={set.title}
        className={data.isFetching && !data.isLoading ? 'opacity-70 transition-opacity' : undefined}
      />

      {total > 0 ? (
        <div className="mt-3 flex items-center justify-between text-sm text-muted-foreground">
          <span className="tabular-nums">{t('pluginPage.data.range', { from, to, total })}</span>
          <div className="flex gap-1">
            <Button
              size="icon"
              variant="outline"
              aria-label={t('pluginPage.data.previous')}
              disabled={page <= 1}
              onClick={() => setPage((p) => p - 1)}
            >
              <ChevronLeft className="h-4 w-4" />
            </Button>
            <Button
              size="icon"
              variant="outline"
              aria-label={t('pluginPage.data.next')}
              disabled={to >= total}
              onClick={() => setPage((p) => p + 1)}
            >
              <ChevronRight className="h-4 w-4" />
            </Button>
          </div>
        </div>
      ) : null}

      <RowSheet
        slug={slug}
        set={set}
        rowKey={openKey}
        onClose={() => setOpenKey(null)}
        onAction={(action, key) => setPending({ action, keys: [key] })}
        onChanged={refresh}
      />
      {creating ? (
        <CreateDialog slug={slug} set={set} onClose={() => setCreating(false)} onCreated={refresh} />
      ) : null}
      {pending ? (
        <ActionDialog
          slug={slug}
          set={set}
          action={pending.action}
          keys={pending.keys}
          onClose={() => setPending(null)}
          onDone={() => {
            setPending(null);
            setSelected(new Set());
            void refresh();
          }}
        />
      ) : null}
    </div>
  );
}

/** One cell, by the column's kind. Unknown kinds are text, so a newer plugin still renders. */
export function Value({ column, value }: { column: Pick<DataColumn, 'kind'>; value: unknown }) {
  if (value === null || value === undefined || value === '') {
    return <span className="text-muted-foreground">—</span>;
  }
  switch (column.kind) {
    case 'boolean':
      return value ? (
        <Check className="h-4 w-4 text-[hsl(var(--success))]" aria-label="yes" />
      ) : (
        <Minus className="h-4 w-4 text-muted-foreground" aria-label="no" />
      );
    case 'datetime':
      return <span className="tabular-nums">{new Date(String(value)).toLocaleString()}</span>;
    case 'number':
      return <span className="tabular-nums">{Number(value).toLocaleString()}</span>;
    case 'bytes':
      return <span className="tabular-nums">{formatBytes(Number(value))}</span>;
    case 'badge':
      return <Badge tone="secondary">{String(value)}</Badge>;
    case 'url':
      return (
        <a
          href={String(value)}
          target="_blank"
          rel="noopener noreferrer"
          className="text-primary underline-offset-2 hover:underline"
          onClick={(e) => e.stopPropagation()}
        >
          {String(value)}
        </a>
      );
    case 'media':
      return <AuthedImage id={String(value)} variant="thumb" className="h-8 w-8 rounded object-cover" />;
    case 'json':
      return <code className="line-clamp-1 font-mono text-xs">{JSON.stringify(value)}</code>;
    default:
      return <span className="line-clamp-2">{typeof value === 'object' ? JSON.stringify(value) : String(value)}</span>;
  }
}

function formatBytes(n: number) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}

const humanize = (key: string) => key.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, (c) => c.toUpperCase());

/** The editable values of a row: exactly the item schema's properties. */
function editable(schema: RJSFSchema | null, values: Record<string, unknown>) {
  const props = Object.keys((schema?.properties as Record<string, unknown>) ?? {});
  return Object.fromEntries(props.filter((p) => p in values).map((p) => [p, values[p]]));
}

function RowSheet({
  slug,
  set,
  rowKey,
  onClose,
  onAction,
  onChanged,
}: {
  slug: string;
  set: DataSet;
  rowKey: string | null;
  onClose: () => void;
  onAction: (action: DataAction, key: string) => void;
  onChanged: () => Promise<unknown>;
}) {
  const { t } = useTranslation();
  const row = useDataRow(slug, set.id, rowKey);
  const [form, setForm] = useState<unknown>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);

  useEffect(() => {
    setForm(row.data ? editable(set.itemSchema, row.data.values) : null);
  }, [row.data, set.itemSchema]);

  const save = useMutation({
    mutationFn: () => dataApi.update(slug, set.id, rowKey!, form ?? {}),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await onChanged();
    },
    onError: (e) => toastApiError(e, t),
  });
  const remove = useMutation({
    mutationFn: () => dataApi.remove(slug, set.id, rowKey!),
    onSuccess: async () => {
      toast.success(t('pluginPage.data.deleted'));
      setConfirmDelete(false);
      onClose();
      await onChanged();
    },
    onError: (e) => toastApiError(e, t),
  });

  const schemaProps = Object.keys((set.itemSchema?.properties as Record<string, unknown>) ?? {});
  const canEdit = set.canUpdate && set.itemSchema !== null;
  const labels = new Map(set.columns.map((c) => [c.key, c]));
  // Everything the form does not show, labelled by its column when there is one. A column that
  // flattens an editable object ("attributes.tier") is already in the form.
  const details = Object.entries(row.data?.values ?? {}).filter(
    ([k]) => !(canEdit && (schemaProps.includes(k) || schemaProps.includes(k.split('.')[0]))),
  );
  const primary = set.columns.find((c) => c.primary) ?? set.columns[0];
  const title = row.data?.title ?? (primary ? String(row.data?.values[primary.key] ?? '') : '');

  return (
    <Sheet open={rowKey !== null} onOpenChange={(o) => !o && onClose()}>
      <SheetContent width="w-[32rem]">
        <SheetHeader>
          <SheetTitle className="truncate pr-6">{title || set.title}</SheetTitle>
          <SheetDescription>{set.title}</SheetDescription>
        </SheetHeader>
        <SheetBody className="space-y-5">
          {row.isLoading ? <CenteredSpinner /> : null}
          {details.length > 0 ? (
            <dl className="grid grid-cols-[minmax(7rem,auto)_1fr] gap-x-4 gap-y-2 text-sm">
              {details.map(([k, v]) => (
                <DetailRow key={k} label={labels.get(k)?.label ?? humanize(k)} kind={labels.get(k)?.kind} value={v} />
              ))}
            </dl>
          ) : null}
          {canEdit && form !== null ? (
            <SchemaForm schema={set.itemSchema!} formData={form} onChange={setForm} widgets={configWidgets} />
          ) : null}
        </SheetBody>
        <SheetFooter className="flex flex-wrap gap-2">
          {set.canWrite
            ? set.actions.map((a) => (
                <Button key={a.id} size="sm" variant="outline" onClick={() => rowKey && onAction(a, rowKey)}>
                  {a.label}
                </Button>
              ))
            : null}
          {set.canDownload && rowKey ? (
            <Button
              size="sm"
              variant="outline"
              onClick={() => dataApi.download(slug, set.id, rowKey).catch((e) => toastApiError(e, t))}
            >
              <Download className="h-4 w-4" aria-hidden />
              {t('pluginPage.data.download')}
            </Button>
          ) : null}
          <span className="flex-1" />
          {set.canDelete ? (
            <Button size="sm" variant="ghost" className="text-destructive" onClick={() => setConfirmDelete(true)}>
              <Trash2 className="h-4 w-4" aria-hidden />
              {t('actions.delete')}
            </Button>
          ) : null}
          {canEdit ? (
            <Button size="sm" disabled={save.isPending || form === null} onClick={() => save.mutate()}>
              {t('actions.save')}
            </Button>
          ) : null}
        </SheetFooter>
      </SheetContent>

      <Dialog open={confirmDelete} onOpenChange={setConfirmDelete}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('pluginPage.data.deleteTitle', { name: title || rowKey })}</DialogTitle>
            <DialogDescription>{t('pluginPage.data.deleteHint')}</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirmDelete(false)}>
              {t('actions.cancel')}
            </Button>
            <Button variant="destructive" disabled={remove.isPending} onClick={() => remove.mutate()}>
              {t('actions.delete')}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Sheet>
  );
}

function DetailRow({ label, kind, value }: { label: string; kind?: string; value: unknown }) {
  const nested = value !== null && typeof value === 'object' && !Array.isArray(value);
  const multiline = typeof value === 'string' && value.includes('\n');
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="min-w-0 break-words">
        {nested ? (
          <dl className="grid grid-cols-[minmax(6rem,auto)_1fr] gap-x-3 gap-y-1">
            {Object.entries(value as Record<string, unknown>).map(([k, v]) => (
              <DetailRow key={k} label={k} value={v} />
            ))}
          </dl>
        ) : multiline ? (
          <pre className="max-h-96 overflow-auto whitespace-pre-wrap rounded bg-muted p-2 font-sans text-xs">{value as string}</pre>
        ) : (
          <Value column={{ kind: kind ?? (typeof value === 'boolean' ? 'boolean' : 'text') }} value={value} />
        )}
      </dd>
    </>
  );
}

function CreateDialog({
  slug,
  set,
  onClose,
  onCreated,
}: {
  slug: string;
  set: DataSet;
  onClose: () => void;
  onCreated: () => Promise<unknown>;
}) {
  const { t } = useTranslation();
  const [values, setValues] = useState<unknown>({});
  const create = useMutation({
    mutationFn: () => dataApi.create(slug, set.id, values ?? {}),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      onClose();
      await onCreated();
    },
    onError: (e) => toastApiError(e, t),
  });
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent wide>
        <DialogHeader>
          <DialogTitle>{t('pluginPage.data.newTitle', { title: set.title })}</DialogTitle>
        </DialogHeader>
        <DialogBody>
          {set.itemSchema ? (
            <SchemaForm schema={set.itemSchema} formData={values} onChange={setValues} widgets={configWidgets} />
          ) : null}
        </DialogBody>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button disabled={create.isPending} onClick={() => create.mutate()}>
            {t('pluginPage.data.create')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/**
 * Runs an action. A safe one without input runs on confirm; one with an input schema asks for
 * it; a dangerous one says what it does and uses the destructive button, because the plugin
 * has told us it cannot be taken back.
 */
function ActionDialog({
  slug,
  set,
  action,
  keys,
  onClose,
  onDone,
}: {
  slug: string;
  set: DataSet;
  action: DataAction;
  keys: string[];
  onClose: () => void;
  onDone: () => void;
}) {
  const { t } = useTranslation();
  const [input, setInput] = useState<unknown>({});
  const run = useMutation({
    mutationFn: () => dataApi.action(slug, set.id, action.id, keys, action.inputSchema ? input : undefined),
    onSuccess: (r) => {
      toast.success(r.message ?? t('pluginPage.data.actionDone', { action: action.label, count: r.affected }));
      onDone();
    },
    onError: (e) => toastApiError(e, t),
  });
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{action.label}</DialogTitle>
          <DialogDescription>
            {action.description ?? t('pluginPage.data.actionOn', { count: keys.length })}
          </DialogDescription>
        </DialogHeader>
        {action.inputSchema ? (
          <DialogBody>
            <SchemaForm schema={action.inputSchema} formData={input} onChange={setInput} widgets={configWidgets} />
          </DialogBody>
        ) : null}
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button
            variant={action.risk === 'dangerous' ? 'destructive' : 'default'}
            disabled={run.isPending}
            onClick={() => run.mutate()}
          >
            {action.label}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
