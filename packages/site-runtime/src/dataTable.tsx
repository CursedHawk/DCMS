import { useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { DataClientContext, DataError } from './data';
import type { ComponentDefinition, ComponentRenderProps } from './registry';
import { useRenderMode } from './renderMode';
import {
  cleanFilter,
  countConditions,
  groupOf,
  isCondition,
  makeGroup,
  newCondition,
  opsFor,
  sameDefinition,
  uiOpOf,
  userAttributeOf,
  userAttributesFor,
  userValue,
  withOp,
  type UiOp,
  type UserAttribute,
  type ViewCondition,
  type ViewDefinition,
  type ViewField,
  type ViewFilter,
} from './views';

/**
 * A Dynamic Apps table on the site (#297): its records with search, sorting, filters and a choice
 * of columns — and saved views. The app's own views are there for everyone; a signed-in user
 * saves their own and shares them, read-only, with everyone signed in, groups or people.
 *
 * Everything goes through the app's public API (`/api/{app}/data/{table}/query`,
 * `/api/{app}/views/{table}`), which decides per call what this visitor may see: the table
 * offers only what that API would answer, and never trusts a view to grant anything.
 */

type Props = Record<string, unknown>;

interface ModelField {
  apiName: string;
  displayName: string;
  type: string;
  target?: string | null;
  options?: string[] | null;
}

interface ModelTable {
  apiName: string;
  displayName: string;
  pluralName?: string | null;
  primaryField?: string | null;
  fields: ModelField[];
}

interface SiteView {
  id: string;
  name: string;
  system: boolean;
  mine: boolean;
  owner?: string | null;
  definition: ViewDefinition;
  sharing?: { everyone: boolean; groups: string[]; users: string[] } | null;
}

interface ViewList {
  signedIn: boolean;
  views: SiteView[];
  defaultView?: string | null;
}

interface RecordPage {
  items: Record<string, unknown>[];
  total: number;
  totalCapped?: boolean;
}

interface Person {
  id: string;
  email: string;
  displayName?: string | null;
}

const LABELS: Record<UiOp, string> = {
  eq: 'is',
  ne: 'is not',
  gt: 'is more than',
  gte: 'is at least',
  lt: 'is less than',
  lte: 'is at most',
  in: 'is any of',
  nin: 'is none of',
  contains: 'contains',
  startsWith: 'starts with',
  isEmpty: 'is empty',
  isNotEmpty: 'is not empty',
};

const CURRENT_USER: Record<UserAttribute, string> = {
  email: 'my email',
  id: 'my user ID',
  groups: 'my groups',
};

const SYSTEM_FIELDS: ViewField[] = [
  { key: 'created_at', label: 'Created', kind: 'date' },
  { key: 'updated_at', label: 'Updated', kind: 'date' },
];

/** What the table can do with a field of the app's public model; null for what a visitor cannot use. */
function viewField(f: ModelField): ViewField | null {
  const base = { key: f.apiName, label: f.displayName };
  switch (f.type) {
    case 'text':
    case 'longText':
    case 'email':
    case 'url':
      return { ...base, kind: 'text' };
    case 'integer':
    case 'decimal':
      return { ...base, kind: 'number' };
    case 'boolean':
      return { ...base, kind: 'boolean' };
    case 'date':
    case 'dateTime':
      return { ...base, kind: 'date', ops: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'isEmpty', 'isNotEmpty'] };
    case 'choice':
      return { ...base, kind: 'choice', options: (f.options ?? []).map((o) => ({ value: o, label: o })) };
    case 'multiChoice':
      return { ...base, kind: 'choice', options: (f.options ?? []).map((o) => ({ value: o, label: o })), ops: ['contains', 'isEmpty', 'isNotEmpty'], sortable: false };
    case 'lookup':
      // Shown by the record it points at; an id is nothing a visitor can sort or filter by.
      return { ...base, kind: 'reference', sortable: false, filterable: false };
    default:
      return null;
  }
}

function text(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

/** `crm`, `deals`: a slug or api name, else nothing is asked for. */
const NAME = /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/;

export function DataTable({ props }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const client = useContext(DataClientContext);
  const instance = text(props.instance).trim();
  const tableName = text(props.table).trim();
  const pageSize = typeof props.pageSize === 'number' ? Math.min(Math.max(Math.round(props.pageSize), 5), 100) : 25;
  const showSearch = props.search !== false;
  const showFilters = props.filters !== false;
  const showViews = props.views !== false;
  const configured = NAME.test(instance) && NAME.test(tableName);
  const base = `/api/${instance}`;

  const [model, setModel] = useState<{ table: ModelTable; tables: ModelTable[] } | 'missing' | null>(null);
  useEffect(() => {
    if (!configured) return;
    let live = true;
    setModel(null);
    client.get(`${base}/_model`).then(
      (json) => {
        const tables = ((json as { tables?: ModelTable[] })?.tables ?? []).filter((t) => Array.isArray(t.fields));
        const table = tables.find((t) => t.apiName === tableName);
        if (live) setModel(table ? { table, tables } : 'missing');
      },
      () => live && setModel('missing'),
    );
    return () => {
      live = false;
    };
  }, [client, base, tableName, configured]);

  const table = model && model !== 'missing' ? model.table : null;
  const fields = useMemo(() => (table ? table.fields.map(viewField).filter((f): f is ViewField => !!f) : []), [table]);
  const filterFields = useMemo(() => [...fields.filter((f) => f.filterable !== false), ...SYSTEM_FIELDS], [fields]);
  const baseline = useMemo<ViewDefinition>(() => {
    const wanted = text(props.columns).split(',').map((c) => c.trim()).filter((c) => fields.some((f) => f.key === c));
    return { columns: wanted.length > 0 ? wanted : fields.slice(0, 6).map((f) => f.key), sort: [] };
  }, [fields, props.columns]);

  // Views: the app's own for anyone, a signed-in user's and those shared with them on top.
  const [views, setViews] = useState<ViewList | null>(null);
  const [viewId, setViewId] = useState<string | null>(null);
  const [definition, setDefinition] = useState<ViewDefinition>(baseline);
  const loadViews = useCallback(
    async (select?: string | null) => {
      const list = (await client.get(`${base}/views/${tableName}`)) as ViewList;
      setViews(list);
      const id = select !== undefined ? select : (list.defaultView ?? null);
      const chosen = list.views.find((v) => v.id === id) ?? null;
      setViewId(chosen?.id ?? null);
      setDefinition(chosen ? chosen.definition : baseline);
    },
    [client, base, tableName, baseline],
  );
  useEffect(() => {
    if (!table) return;
    setDefinition(baseline);
    setViewId(null);
    if (showViews) loadViews().catch(() => setViews(null));
  }, [table, showViews, loadViews, baseline]);

  const current = views?.views.find((v) => v.id === viewId) ?? null;
  const dirty = !sameDefinition(definition, current ? current.definition : baseline);

  // The records.
  const [search, setSearch] = useState('');
  const [term, setTerm] = useState('');
  const [page, setPage] = useState(1);
  const [result, setResult] = useState<{ state: 'loading' } | { state: 'error'; status: number } | { state: 'ready'; page: RecordPage }>({ state: 'loading' });
  useEffect(() => {
    const t = setTimeout(() => setTerm(search.trim()), 300);
    return () => clearTimeout(t);
  }, [search]);
  const filter = cleanFilter(definition.filter);
  const filterJson = JSON.stringify(filter ?? null);
  const sortJson = JSON.stringify(definition.sort);
  const shown = definition.columns.map((c) => fields.find((f) => f.key === c)).filter((f): f is ViewField => !!f);
  const shownKey = shown.map((f) => f.key).join(',');
  useEffect(() => setPage(1), [filterJson, sortJson, term]);
  useEffect(() => {
    if (!table) return;
    let live = true;
    setResult({ state: 'loading' });
    const lookups = shown.filter((f) => f.kind === 'reference').map((f) => f.key);
    client
      .post(`${base}/data/${tableName}/query`, {
        page,
        pageSize,
        search: term || undefined,
        filter: filter ?? undefined,
        sort: definition.sort,
        select: shown.map((f) => f.key),
        expand: lookups,
      })
      .then(
        (json) => live && setResult({ state: 'ready', page: json as RecordPage }),
        (e: unknown) => live && setResult({ state: 'error', status: e instanceof DataError ? e.status : 0 }),
      );
    return () => {
      live = false;
    };
    // filter and sort by their JSON: a re-render that rebuilds them equal asks nothing again.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, base, tableName, table, page, pageSize, term, filterJson, sortJson, shownKey]);

  const [panel, setPanel] = useState<'filter' | 'columns' | 'save' | 'share' | null>(null);
  const toggle = (p: typeof panel) => setPanel((open) => (open === p ? null : p));

  if (!configured) {
    return mode === 'edit' ? <div className="dcms-editor-note">Choose the app and the table this shows.</div> : null;
  }
  if (model === 'missing') {
    return mode === 'edit' ? (
      <div className="dcms-editor-note">
        “{tableName}” is not a table the site can read in the app “{instance}”. Check the names, and that the table is published with public access.
      </div>
    ) : (
      <p className="dcms-form-error">This table is not available.</p>
    );
  }
  if (!table) return <div className="dcms-data-table" aria-busy="true" />;

  // Saving needs a signed-in user and a site to save on; the canvas only shows.
  const canSave = !!views?.signedIn && !!client.send && mode === 'live';
  const lookupTarget = (key: string) => {
    const target = table.fields.find((f) => f.apiName === key)?.target;
    return model?.tables.find((t) => t.apiName === target);
  };
  const sortOf = (key: string) => definition.sort.find((s) => s.field === key)?.direction;
  const sortBy = (key: string) => {
    const now = sortOf(key);
    setDefinition({ ...definition, sort: now === 'asc' ? [{ field: key, direction: 'desc' }] : now === 'desc' ? [] : [{ field: key, direction: 'asc' }] });
  };
  const conditions = countConditions(definition.filter);

  return (
    <div className="dcms-data-table">
      <div className="dcms-data-table-toolbar">
        {showViews && views && (
          <label className="dcms-data-table-view">
            <span className="dcms-visually-hidden">View</span>
            <select
              value={viewId ?? ''}
              onChange={(e) => {
                const next = views.views.find((v) => v.id === e.target.value) ?? null;
                setViewId(next?.id ?? null);
                setDefinition(next ? next.definition : baseline);
                setPanel(null);
              }}
            >
              <option value="">All {table.pluralName ?? table.displayName}</option>
              <ViewOptions label="The app's views" views={views.views.filter((v) => v.system)} />
              <ViewOptions label="My views" views={views.views.filter((v) => v.mine)} />
              <ViewOptions label="Shared with me" views={views.views.filter((v) => !v.system && !v.mine)} owner />
            </select>
          </label>
        )}
        {showSearch && (
          <input
            type="search"
            className="dcms-data-table-search"
            placeholder="Search"
            aria-label={`Search ${table.pluralName ?? table.displayName}`}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        )}
        {showFilters && (
          <>
            <button type="button" className="dcms-button dcms-button-secondary dcms-button-sm" aria-expanded={panel === 'filter'} onClick={() => toggle('filter')}>
              Filter{conditions > 0 ? ` (${conditions})` : ''}
            </button>
            <button type="button" className="dcms-button dcms-button-secondary dcms-button-sm" aria-expanded={panel === 'columns'} onClick={() => toggle('columns')}>
              Columns
            </button>
          </>
        )}
        {showViews && canSave && (
          <>
            {current?.mine && dirty && (
              <button
                type="button"
                className="dcms-button dcms-button-primary dcms-button-sm"
                onClick={() => send('PUT', `${base}/views/${tableName}/${current.id}`, { name: current.name, definition }).then(() => loadViews(current.id))}
              >
                Save
              </button>
            )}
            <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" aria-expanded={panel === 'save'} onClick={() => toggle('save')}>
              Save as…
            </button>
            {current?.mine && (
              <>
                <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" aria-expanded={panel === 'share'} onClick={() => toggle('share')}>
                  Share
                </button>
                <button
                  type="button"
                  className="dcms-button dcms-button-ghost dcms-button-sm"
                  onClick={() => {
                    if (window.confirm(`Delete the view “${current.name}”?`)) send('DELETE', `${base}/views/${tableName}/${current.id}`).then(() => loadViews(null));
                  }}
                >
                  Delete
                </button>
              </>
            )}
            {(views?.defaultView ?? null) !== viewId && (
              <button
                type="button"
                className="dcms-button dcms-button-ghost dcms-button-sm"
                onClick={() => send('PUT', `${base}/views/${tableName}/default`, { viewKey: viewId }).then(() => loadViews(viewId))}
              >
                Open with this view
              </button>
            )}
          </>
        )}
      </div>
      {mode === 'edit' && showViews && <div className="dcms-editor-note">Signed-in visitors can save views here and share them; the canvas only shows the table.</div>}

      {panel === 'filter' && (
        <div className="dcms-data-table-panel">
          <FilterGroup
            group={definition.filter && !isCondition(definition.filter) && !('not' in definition.filter) ? definition.filter : makeGroup('and', definition.filter ? [definition.filter] : [])}
            fields={filterFields}
            depth={1}
            onChange={(next) => setDefinition({ ...definition, filter: next })}
          />
        </div>
      )}
      {panel === 'columns' && (
        <div className="dcms-data-table-panel">
          <ColumnChooser fields={fields} columns={definition.columns} onChange={(columns) => setDefinition({ ...definition, columns })} />
        </div>
      )}
      {panel === 'save' && (
        <div className="dcms-data-table-panel">
          <SaveAs
            onSave={async (name) => {
              const saved = (await send('POST', `${base}/views/${tableName}`, { name, definition })) as SiteView;
              await loadViews(saved.id);
              setPanel(null);
            }}
          />
        </div>
      )}
      {panel === 'share' && current?.mine && (
        <div className="dcms-data-table-panel">
          <ShareView
            base={base}
            view={current}
            onSave={async (sharing) => {
              await send('PUT', `${base}/views/${tableName}/${current.id}/sharing`, sharing);
              await loadViews(current.id);
              setPanel(null);
            }}
          />
        </div>
      )}

      <div className="dcms-data-table-scroll">
        <table>
          <thead>
            <tr>
              {shown.map((f) => {
                const dir = sortOf(f.key);
                return (
                  <th key={f.key} scope="col" aria-sort={dir === 'asc' ? 'ascending' : dir === 'desc' ? 'descending' : undefined}>
                    {f.sortable === false ? (
                      f.label
                    ) : (
                      <button type="button" onClick={() => sortBy(f.key)}>
                        {f.label}
                        <span aria-hidden="true">{dir === 'asc' ? ' ▲' : dir === 'desc' ? ' ▼' : ''}</span>
                      </button>
                    )}
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody>
            {result.state === 'ready' &&
              result.page.items.map((row) => (
                <tr key={String(row.id)}>
                  {shown.map((f) => (
                    <td key={f.key}>
                      <Cell field={f} value={row[f.key]} target={f.kind === 'reference' ? lookupTarget(f.key) : undefined} />
                    </td>
                  ))}
                </tr>
              ))}
          </tbody>
        </table>
      </div>
      {result.state === 'loading' && <p className="dcms-data-table-state" aria-busy="true">Loading…</p>}
      {result.state === 'error' && (
        <p className="dcms-data-table-state dcms-form-error">
          {result.status === 401 ? 'Sign in to see these records.' : result.status === 400 ? 'This view cannot be shown any more; choose another.' : 'The records could not be loaded.'}
        </p>
      )}
      {result.state === 'ready' && result.page.items.length === 0 && <p className="dcms-data-table-state">Nothing matches.</p>}
      {result.state === 'ready' && <Pager page={page} pageSize={pageSize} total={result.page.total} capped={!!result.page.totalCapped} full={result.page.items.length === pageSize} onPage={setPage} />}
    </div>
  );

  function send(method: 'POST' | 'PUT' | 'DELETE', path: string, body?: unknown): Promise<unknown> {
    return client.send!(method, path, body).catch((e: unknown) => {
      const message = e instanceof DataError && e.body && typeof e.body === 'object' && 'error' in e.body ? String((e.body as { error: unknown }).error) : 'That did not work.';
      window.alert(message);
      throw e;
    });
  }
}

function ViewOptions({ label, views, owner }: { label: string; views: SiteView[]; owner?: boolean }) {
  if (views.length === 0) return null;
  return (
    <optgroup label={label}>
      {views.map((v) => (
        <option key={v.id} value={v.id}>
          {v.name}
          {owner && v.owner ? ` — ${v.owner}` : ''}
        </option>
      ))}
    </optgroup>
  );
}

function Cell({ field, value, target }: { field: ViewField; value: unknown; target?: ModelTable }) {
  if (value === null || value === undefined || value === '') return <span className="dcms-data-table-empty">—</span>;
  if (field.kind === 'reference' && typeof value === 'object') {
    const record = value as Record<string, unknown>;
    const primary = target?.primaryField ? record[target.primaryField] : undefined;
    return <>{String(primary ?? record.id ?? '')}</>;
  }
  if (Array.isArray(value)) return <>{value.join(', ')}</>;
  if (field.kind === 'boolean') return <>{value === true ? '✓' : '—'}</>;
  if (field.kind === 'number' && typeof value === 'number') return <>{value.toLocaleString()}</>;
  if (field.kind === 'date' && typeof value === 'string') {
    const d = new Date(value);
    if (Number.isNaN(d.getTime())) return <>{value}</>;
    return <>{value.length > 10 ? d.toLocaleString() : d.toLocaleDateString()}</>;
  }
  return <>{typeof value === 'object' ? JSON.stringify(value) : String(value)}</>;
}

function Pager({ page, pageSize, total, capped, full, onPage }: { page: number; pageSize: number; total: number; capped: boolean; full: boolean; onPage: (p: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const hasNext = capped ? full : page < pages;
  if (page === 1 && !hasNext) return null;
  return (
    <nav className="dcms-pager" aria-label="Pages">
      <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" aria-label="Previous page" disabled={page <= 1} onClick={() => onPage(page - 1)}>
        ‹
      </button>
      <span aria-live="polite">
        {page} / {capped ? `${pages}+` : pages}
      </span>
      <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" aria-label="Next page" disabled={!hasNext} onClick={() => onPage(page + 1)}>
        ›
      </button>
    </nav>
  );
}

function FilterGroup({ group, fields, depth, onChange, onRemove }: { group: ViewFilter; fields: ViewField[]; depth: number; onChange: (next: ViewFilter) => void; onRemove?: () => void }) {
  const { mode, items } = groupOf(group);
  const set = (next: ViewFilter[]) => onChange(makeGroup(mode, next));
  const replace = (index: number, next: ViewFilter) => set(items.map((x, i) => (i === index ? next : x)));
  const remove = (index: number) => set(items.filter((_, i) => i !== index));
  return (
    <fieldset className={depth > 1 ? 'dcms-data-table-group dcms-data-table-subgroup' : 'dcms-data-table-group'}>
      <legend>
        <label>
          Match{' '}
          <select value={mode} onChange={(e) => onChange(makeGroup(e.target.value as 'and' | 'or', items))}>
            <option value="and">all of</option>
            <option value="or">any of</option>
          </select>
        </label>
        {onRemove && (
          <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" onClick={onRemove}>
            Remove group
          </button>
        )}
      </legend>
      {items.map((item, index) =>
        isCondition(item) ? (
          <Condition key={index} condition={item} fields={fields} onChange={(next) => replace(index, next)} onRemove={() => remove(index)} />
        ) : (
          <FilterGroup key={index} group={item} fields={fields} depth={depth + 1} onChange={(next) => replace(index, next)} onRemove={() => remove(index)} />
        ),
      )}
      <div className="dcms-data-table-row">
        <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" disabled={fields.length === 0} onClick={() => set([...items, newCondition(fields[0])])}>
          + Condition
        </button>
        {depth < 3 && (
          <button
            type="button"
            className="dcms-button dcms-button-ghost dcms-button-sm"
            disabled={fields.length === 0}
            onClick={() => set([...items, makeGroup(mode === 'and' ? 'or' : 'and', [newCondition(fields[0])])])}
          >
            + Group
          </button>
        )}
      </div>
    </fieldset>
  );
}

function Condition({ condition, fields, onChange, onRemove }: { condition: ViewCondition; fields: ViewField[]; onChange: (next: ViewCondition) => void; onRemove: () => void }) {
  const field = fields.find((f) => f.key === condition.field);
  const op = uiOpOf(condition);
  const ops = field ? (field.ops ?? opsFor(field.kind)) : [op];
  // A text field can be compared with whoever looks at the view, signed in (#302): a shared view
  // then shows each person their own rows.
  const users = field?.kind === 'text' ? userAttributesFor(op, ['email', 'id', 'groups']) : [];
  const user = userAttributeOf(condition.value);
  return (
    <div className="dcms-data-table-row">
      <select
        aria-label="Field"
        value={condition.field}
        onChange={(e) => {
          const next = fields.find((f) => f.key === e.target.value);
          if (next) onChange(newCondition(next));
        }}
      >
        {fields.map((f) => (
          <option key={f.key} value={f.key}>
            {f.label}
          </option>
        ))}
      </select>
      <select aria-label="Condition" value={op} onChange={(e) => onChange(withOp(condition, e.target.value as UiOp))}>
        {ops.map((o) => (
          <option key={o} value={o}>
            {LABELS[o]}
          </option>
        ))}
      </select>
      {users.length > 0 && (
        <select
          aria-label="Compare with"
          value={user ?? 'typed'}
          onChange={(e) => onChange({ ...condition, value: e.target.value === 'typed' ? undefined : userValue(e.target.value as UserAttribute) })}
        >
          <option value="typed">a value</option>
          {users.map((a) => (
            <option key={a} value={a}>
              {CURRENT_USER[a]}
            </option>
          ))}
        </select>
      )}
      {field && !user && op !== 'isEmpty' && op !== 'isNotEmpty' && <Value field={field} condition={condition} onChange={(value) => onChange({ ...condition, value })} />}
      <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" aria-label="Remove condition" onClick={onRemove}>
        ×
      </button>
    </div>
  );
}

function Value({ field, condition, onChange }: { field: ViewField; condition: ViewCondition; onChange: (value: unknown) => void }) {
  const value = condition.value;
  const list = condition.op === 'in' || condition.op === 'nin';
  if (field.kind === 'boolean') {
    return (
      <select aria-label="Value" value={value === false ? 'false' : 'true'} onChange={(e) => onChange(e.target.value === 'true')}>
        <option value="true">yes</option>
        <option value="false">no</option>
      </select>
    );
  }
  if (field.kind === 'choice' && field.options) {
    if (list) {
      const picked = Array.isArray(value) ? (value as string[]) : [];
      return (
        <span className="dcms-data-table-row" role="group" aria-label="Values">
          {field.options.map((o) => (
            <label key={o.value} className="dcms-field-check">
              <input
                type="checkbox"
                checked={picked.includes(o.value)}
                onChange={(e) => onChange(e.target.checked ? [...picked, o.value] : picked.filter((v) => v !== o.value))}
              />
              {o.label}
            </label>
          ))}
        </span>
      );
    }
    return (
      <select aria-label="Value" value={typeof value === 'string' ? value : ''} onChange={(e) => onChange(e.target.value)}>
        <option value="" disabled>
          Choose…
        </option>
        {field.options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    );
  }
  if (list) {
    // One value per comma: "Prague, Brno".
    return (
      <input
        aria-label="Values, separated by commas"
        value={Array.isArray(value) ? value.join(', ') : ''}
        onChange={(e) => onChange(e.target.value.split(',').map((v) => v.trim()).filter(Boolean))}
      />
    );
  }
  if (field.kind === 'number') {
    return <input type="number" aria-label="Value" value={typeof value === 'number' ? value : ''} onChange={(e) => onChange(e.target.value === '' ? undefined : Number(e.target.value))} />;
  }
  return <input type={field.kind === 'date' ? 'date' : 'text'} aria-label="Value" value={typeof value === 'string' ? value : ''} onChange={(e) => onChange(e.target.value)} />;
}

function ColumnChooser({ fields, columns, onChange }: { fields: ViewField[]; columns: string[]; onChange: (columns: string[]) => void }) {
  const move = (key: string, by: number) => {
    const i = columns.indexOf(key);
    const j = i + by;
    if (i < 0 || j < 0 || j >= columns.length) return;
    const next = [...columns];
    [next[i], next[j]] = [next[j], next[i]];
    onChange(next);
  };
  // The shown ones in their order, then the rest.
  const ordered = [...columns.map((c) => fields.find((f) => f.key === c)).filter((f): f is ViewField => !!f), ...fields.filter((f) => !columns.includes(f.key))];
  return (
    <ul className="dcms-data-table-columns">
      {ordered.map((f) => {
        const on = columns.includes(f.key);
        return (
          <li key={f.key} className="dcms-data-table-row">
            <label className="dcms-field-check">
              <input type="checkbox" checked={on} onChange={() => onChange(on ? columns.filter((c) => c !== f.key) : [...columns, f.key])} />
              {f.label}
            </label>
            {on && (
              <>
                <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" aria-label={`Move ${f.label} left`} onClick={() => move(f.key, -1)}>
                  ↑
                </button>
                <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" aria-label={`Move ${f.label} right`} onClick={() => move(f.key, 1)}>
                  ↓
                </button>
              </>
            )}
          </li>
        );
      })}
    </ul>
  );
}

function SaveAs({ onSave }: { onSave: (name: string) => Promise<void> }) {
  const [name, setName] = useState('');
  return (
    <form
      className="dcms-data-table-row"
      onSubmit={(e) => {
        e.preventDefault();
        if (name.trim()) onSave(name.trim()).catch(() => undefined);
      }}
    >
      <label className="dcms-field">
        <span>Name of the new view</span>
        <input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} required />
      </label>
      <button type="submit" className="dcms-button dcms-button-primary dcms-button-sm">
        Save view
      </button>
    </form>
  );
}

/** Whom a view goes to: everyone signed in, groups, people — all from the site's own directory. */
function ShareView({ base, view, onSave }: { base: string; view: SiteView; onSave: (sharing: { everyone: boolean; groups: string[]; users: string[] }) => Promise<void> }) {
  const client = useContext(DataClientContext);
  const [everyone, setEveryone] = useState(view.sharing?.everyone ?? false);
  const [groups, setGroups] = useState<string[]>(view.sharing?.groups ?? []);
  const [people, setPeople] = useState<Person[]>([]);
  const [allGroups, setAllGroups] = useState<{ id: string; name: string }[]>([]);
  const [query, setQuery] = useState('');
  const [found, setFound] = useState<Person[]>([]);
  useEffect(() => {
    client.get(`${base}/views/_groups`).then((json) => setAllGroups((json as { groups?: { id: string; name: string }[] }).groups ?? []), () => undefined);
    const ids = view.sharing?.users ?? [];
    if (ids.length > 0) client.get(`${base}/views/_people?ids=${ids.join(',')}`).then((json) => setPeople((json as { items?: Person[] }).items ?? []), () => undefined);
  }, [client, base, view]);
  useEffect(() => {
    if (query.trim().length < 2) {
      setFound([]);
      return;
    }
    const t = setTimeout(() => {
      client.get(`${base}/views/_people?search=${encodeURIComponent(query.trim())}`).then((json) => setFound((json as { items?: Person[] }).items ?? []), () => setFound([]));
    }, 250);
    return () => clearTimeout(t);
  }, [client, base, query]);
  const name = (p: Person) => (p.displayName ? `${p.displayName} (${p.email})` : p.email);
  return (
    <form
      className="dcms-data-table-share"
      onSubmit={(e) => {
        e.preventDefault();
        onSave({ everyone, groups, users: people.map((p) => p.id) }).catch(() => undefined);
      }}
    >
      <label className="dcms-field-check">
        <input type="checkbox" checked={everyone} onChange={(e) => setEveryone(e.target.checked)} />
        Everyone signed in
      </label>
      {allGroups.length > 0 && (
        <fieldset className="dcms-field-group dcms-field">
          <legend>Groups</legend>
          {allGroups.map((g) => (
            <label key={g.id} className="dcms-field-check">
              <input type="checkbox" checked={groups.includes(g.id)} onChange={(e) => setGroups(e.target.checked ? [...groups, g.id] : groups.filter((x) => x !== g.id))} />
              {g.name}
            </label>
          ))}
        </fieldset>
      )}
      <div className="dcms-field">
        <label htmlFor={`share-${view.id}`}>People</label>
        <input id={`share-${view.id}`} type="search" placeholder="Name or email" value={query} onChange={(e) => setQuery(e.target.value)} />
        <ul className="dcms-data-table-people">
          {found
            .filter((p) => !people.some((x) => x.id === p.id))
            .map((p) => (
              <li key={p.id}>
                <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" onClick={() => setPeople([...people, p])}>
                  + {name(p)}
                </button>
              </li>
            ))}
        </ul>
        <ul className="dcms-data-table-people">
          {people.map((p) => (
            <li key={p.id}>
              {name(p)}{' '}
              <button type="button" className="dcms-button dcms-button-ghost dcms-button-sm" aria-label={`Stop sharing with ${name(p)}`} onClick={() => setPeople(people.filter((x) => x.id !== p.id))}>
                ×
              </button>
            </li>
          ))}
        </ul>
      </div>
      <button type="submit" className="dcms-button dcms-button-primary dcms-button-sm">
        Save sharing
      </button>
    </form>
  );
}

export const DATA_TABLE_COMPONENTS: readonly ComponentDefinition[] = [
  {
    type: 'dcms.data-table',
    version: 1,
    label: 'Data table',
    description: 'Records of one of your Dynamic Apps tables, with search, sorting, filters and columns. Signed-in visitors save views and share them.',
    category: 'Data',
    keywords: ['table', 'grid', 'records', 'list', 'dynamic apps', 'crm', 'filter', 'sort', 'views'],
    component: DataTable,
    // Its controls are for using, also on the canvas: sorting and filtering there shows the real thing.
    interaction: 'interactive',
    props: [
      { kind: 'text', name: 'instance', label: 'App', required: true, maxLength: 64, group: 'data', description: 'The Dynamic Apps instance the table belongs to, e.g. “crm”.' },
      { kind: 'text', name: 'table', label: 'Table', required: true, maxLength: 64, group: 'data', description: 'The table’s API name, e.g. “deals”. The site shows it only if it has public read access, or to users whose role allows it.' },
      {
        kind: 'text',
        name: 'columns',
        label: 'Columns',
        maxLength: 500,
        group: 'data',
        description: 'Field API names, separated by commas, shown until a view is chosen. Empty: the first six fields.',
      },
      { kind: 'number', name: 'pageSize', label: 'Rows per page', default: 25, min: 5, max: 100, step: 1, group: 'data', description: 'How many records one page shows.' },
      { kind: 'boolean', name: 'search', label: 'Search box', default: true, group: 'behaviour', description: 'Visitors can search the records.' },
      { kind: 'boolean', name: 'filters', label: 'Filters and columns', default: true, group: 'behaviour', description: 'Visitors can filter the records and choose the columns.' },
      {
        kind: 'boolean',
        name: 'views',
        label: 'Saved views',
        default: true,
        group: 'behaviour',
        description: 'Visitors can pick the app’s own views; signed-in users also save their own and share them.',
      },
    ],
  },
];

