/**
 * Table views (#278, #297): what a view is, as the server stores it, and the helpers every
 * editor of one shares — the console's and a site's data table alike. The filter is the server's
 * tree — `{ and }`, `{ or }`, `{ not }` or a comparison — so what a builder makes is sent unchanged.
 *
 * Plain data and functions only: it is vendored into sites with the rest of the runtime, and
 * `@dcms/ui` re-exports it for the console.
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
  /** The operators the table takes for it, when fewer than its kind's (`opsFor`). */
  ops?: UiOp[];
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

/**
 * "The current user" as a value (#302): `{ $user: 'email' | 'id' | 'groups' }`. A view keeps the
 * token, not a person; the server puts whoever is looking in its place on every query, so a
 * shared view shows each viewer their own rows. Email and id compare as one value (is, is not,
 * contains, starts with); groups are a list (is any of, is none of).
 */
export type UserAttribute = 'email' | 'id' | 'groups';

export function userValue(attribute: UserAttribute): { $user: UserAttribute } {
  return { $user: attribute };
}

export function userAttributeOf(value: unknown): UserAttribute | null {
  const a = value && typeof value === 'object' && !Array.isArray(value) ? (value as { $user?: unknown }).$user : undefined;
  return a === 'email' || a === 'id' || a === 'groups' ? a : null;
}

/** The current-user values an operator takes: one person's email or id, or their groups for a list. */
export function userAttributesFor(op: UiOp, offered: readonly UserAttribute[]): UserAttribute[] {
  const list = op === 'in' || op === 'nin';
  const single = op === 'eq' || op === 'ne' || op === 'contains' || op === 'startsWith';
  return offered.filter((a) => (a === 'groups' ? list : single));
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
  const user = userAttributeOf(c.value);
  // The current user stays while the operator still takes them; otherwise the value starts over.
  if (user) return { field: c.field, op, value: userAttributesFor(op, [user]).length > 0 ? c.value : undefined };
  let value: unknown = c.op === 'isNull' ? undefined : c.value;
  if (list && !Array.isArray(value)) value = value == null || value === '' ? [] : [value];
  if (!list && Array.isArray(value)) value = value[0];
  return { field: c.field, op, value };
}

export function newCondition(field: ViewField): ViewCondition {
  if (field.kind === 'boolean') return { field: field.key, op: 'eq', value: true };
  return withOp({ field: field.key, op: 'eq' }, (field.ops ?? opsFor(field.kind))[0]);
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
  if (c.op === 'isNull' || userAttributeOf(c.value)) return true;
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
