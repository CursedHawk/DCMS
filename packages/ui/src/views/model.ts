/**
 * Saved table views (#278): what a view is, as the server stores it, and the helpers the
 * editors share. The filter is the server's tree — `{ and }`, `{ or }`, `{ not }` or a
 * comparison — so what the builder makes is sent unchanged.
 */

export type ViewFieldKind = 'text' | 'number' | 'boolean' | 'date' | 'tags' | 'choice' | 'reference';

export interface ViewField {
  key: string;
  label: string;
  kind: ViewFieldKind;
  /** A choice field's values. */
  options?: { value: string; label: string }[];
  /** False when the table cannot filter on it (a plugin data set's undeclared column). */
  filterable?: boolean;
  /** False when the table cannot sort on it. */
  sortable?: boolean;
}

export type ViewOp = 'eq' | 'ne' | 'gt' | 'gte' | 'lt' | 'lte' | 'in' | 'nin' | 'contains' | 'startsWith' | 'isNull';

export interface ViewCondition {
  field: string;
  op: ViewOp;
  value?: unknown;
}

export type ViewFilter = { and: ViewFilter[] } | { or: ViewFilter[] } | { not: ViewFilter } | ViewCondition;

export interface ViewSort {
  field: string;
  direction: 'asc' | 'desc';
}

export interface ViewDefinition {
  columns: string[];
  sort: ViewSort[];
  filter?: ViewFilter | null;
}

export interface SavedView {
  id: string;
  name: string;
  /** The caller owns it, so may change, share and delete it. */
  mine: boolean;
  /** Who owns a view shared with the caller. */
  owner?: string | null;
  definition: ViewDefinition;
  sharedWithWorkspace?: boolean;
  /** Only on the owner's own views. */
  sharedWith?: { userIds: string[]; roleIds: string[] } | null;
  /** One of the table's built-in views (a Dynamic Apps maker view): nobody edits it here. */
  system?: boolean;
}

/** What a condition row offers: the server's operators, plus "is empty" / "is not empty" (isNull true/false). */
export type UiOp = Exclude<ViewOp, 'isNull'> | 'isEmpty' | 'isNotEmpty';

export function opsFor(kind: ViewFieldKind): UiOp[] {
  switch (kind) {
    case 'text':
      return ['contains', 'eq', 'ne', 'startsWith', 'isEmpty', 'isNotEmpty'];
    case 'number':
      return ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'isEmpty', 'isNotEmpty'];
    case 'date':
      return ['eq', 'gt', 'gte', 'lt', 'lte', 'isEmpty', 'isNotEmpty'];
    case 'boolean':
      return ['eq'];
    case 'tags':
      return ['contains', 'in', 'nin', 'isEmpty', 'isNotEmpty'];
    case 'choice':
      return ['in', 'nin', 'eq', 'ne', 'isEmpty', 'isNotEmpty'];
    case 'reference':
      return ['eq', 'ne', 'isEmpty', 'isNotEmpty'];
  }
}

export function uiOpOf(c: ViewCondition): UiOp {
  if (c.op === 'isNull') return c.value === false ? 'isNotEmpty' : 'isEmpty';
  return c.op;
}

/** A condition for the operator picked, keeping the value when it still fits. */
export function withOp(c: ViewCondition, op: UiOp): ViewCondition {
  if (op === 'isEmpty') return { field: c.field, op: 'isNull', value: true };
  if (op === 'isNotEmpty') return { field: c.field, op: 'isNull', value: false };
  const list = op === 'in' || op === 'nin';
  let value: unknown = c.op === 'isNull' ? undefined : c.value;
  if (list && !Array.isArray(value)) value = value == null || value === '' ? [] : [value];
  if (!list && Array.isArray(value)) value = value[0];
  return { field: c.field, op, value };
}

export function newCondition(field: ViewField): ViewCondition {
  if (field.kind === 'boolean') return { field: field.key, op: 'eq', value: true };
  return withOp({ field: field.key, op: 'eq' }, opsFor(field.kind)[0]);
}

export function isCondition(f: ViewFilter): f is ViewCondition {
  return 'field' in f;
}

export function groupOf(f: ViewFilter | null | undefined): { mode: 'and' | 'or'; items: ViewFilter[] } {
  if (!f) return { mode: 'and', items: [] };
  if ('and' in f) return { mode: 'and', items: f.and };
  if ('or' in f) return { mode: 'or', items: f.or };
  return { mode: 'and', items: [f] };
}

export function makeGroup(mode: 'and' | 'or', items: ViewFilter[]): ViewFilter {
  return mode === 'and' ? { and: items } : { or: items };
}

function complete(c: ViewCondition): boolean {
  if (c.op === 'isNull') return true;
  if (c.op === 'in' || c.op === 'nin') return Array.isArray(c.value) && c.value.length > 0;
  return c.value !== undefined && c.value !== null && c.value !== '';
}

/**
 * The filter to send: conditions still being filled in, and groups left empty by that, are
 * dropped rather than sent half-made. Undefined when nothing is left.
 */
export function cleanFilter(f: ViewFilter | null | undefined): ViewFilter | undefined {
  if (!f) return undefined;
  if (isCondition(f)) return complete(f) ? f : undefined;
  if ('not' in f) {
    const inner = cleanFilter(f.not);
    return inner ? { not: inner } : undefined;
  }
  const { mode, items } = groupOf(f);
  const kept = items.map(cleanFilter).filter((x): x is ViewFilter => !!x);
  return kept.length === 0 ? undefined : makeGroup(mode, kept);
}

export function countConditions(f: ViewFilter | null | undefined): number {
  if (!f) return 0;
  if (isCondition(f)) return 1;
  if ('not' in f) return countConditions(f.not);
  return groupOf(f).items.reduce((n, x) => n + countConditions(x), 0);
}

/** Whether two definitions show the same thing, ignoring half-made conditions. */
export function sameDefinition(a: ViewDefinition, b: ViewDefinition): boolean {
  const norm = (d: ViewDefinition) => JSON.stringify({ columns: d.columns, sort: d.sort, filter: cleanFilter(d.filter) ?? null });
  return norm(a) === norm(b);
}
