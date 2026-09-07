import { ArrowDown, ArrowUp, ChevronsUpDown } from 'lucide-react';
import { useMemo, useState } from 'react';
import { cn } from '../cn';
import { Checkbox } from '../ui/checkbox';
import { Skeleton } from '../ui/skeleton';

export interface Column<T> {
  id: string;
  header: React.ReactNode;
  cell: (row: T) => React.ReactNode;
  /**
   * Makes the column sortable. Returning `null` sorts that row last regardless of direction —
   * an empty cell is not "smallest", it is absent, and burying it at the bottom is what a
   * reader expects in both directions.
   */
  sortValue?: (row: T) => string | number | null;
  align?: 'left' | 'right';
  /** Fixes the column width. Anything else shares what is left. */
  width?: string;
  /**
   * The card's heading below the table breakpoint. Exactly one column should set it; without
   * one the first column is used.
   */
  primary?: boolean;
  /** Left out of the card layout — a column that only makes sense beside its neighbours. */
  hideOnCard?: boolean;
  /** A short screen-reader name where `header` is an icon or is empty. */
  srHeader?: string;
}

export interface DataTableLabels {
  loading: string;
  selectRow: string;
  selectAll: string;
  sortBy: (column: string) => string;
}

const DEFAULTS: DataTableLabels = {
  loading: 'Loading…',
  selectRow: 'Select row',
  selectAll: 'Select all',
  sortBy: (column) => `Sort by ${column}`,
};

type Direction = 'asc' | 'desc';

/**
 * The table every list screen in both consoles is built from.
 *
 * <p><b>Below `md` it stops being a table and becomes cards.</b> That is the whole reason this
 * exists rather than more `<Table>` markup: a seven-column table on a phone is either a
 * horizontal scroll nobody finds or a squeeze that makes every column unreadable. Each row
 * becomes a card with one column as its heading and the rest as labelled fields, which is the
 * same information in the shape a narrow screen can hold.</p>
 *
 * <p>Sorting is client-side and lives here. Every list in these consoles fetches a page and
 * sorts what it has; a server-sorted table would need its own component and neither app has
 * one yet.</p>
 */
export function DataTable<T>({
  rows,
  columns,
  rowKey,
  isLoading,
  error,
  empty,
  onRowClick,
  rowClassName,
  selection,
  defaultSort,
  labels: partialLabels,
  caption,
  className,
}: {
  rows: readonly T[] | undefined;
  columns: readonly Column<T>[];
  rowKey: (row: T) => string;
  isLoading?: boolean;
  /** Rendered instead of the rows. A failed list must not look like an empty one. */
  error?: React.ReactNode;
  empty?: React.ReactNode;
  onRowClick?: (row: T) => void;
  rowClassName?: (row: T) => string | undefined;
  selection?: {
    selected: ReadonlySet<string>;
    onChange: (next: Set<string>) => void;
  };
  defaultSort?: { columnId: string; direction?: Direction };
  labels?: Partial<DataTableLabels>;
  /** Describes the table for screen readers. Visually hidden. */
  caption?: string;
  className?: string;
}) {
  const labels = { ...DEFAULTS, ...partialLabels };
  const [sort, setSort] = useState<{ columnId: string; direction: Direction } | null>(
    defaultSort ? { columnId: defaultSort.columnId, direction: defaultSort.direction ?? 'asc' } : null,
  );

  const sorted = useMemo(() => {
    if (!rows) return undefined;
    if (!sort) return rows;
    const column = columns.find((c) => c.id === sort.columnId);
    if (!column?.sortValue) return rows;
    const factor = sort.direction === 'asc' ? 1 : -1;
    return [...rows].sort((a, b) => {
      const left = column.sortValue!(a);
      const right = column.sortValue!(b);
      // Absent values sink in both directions; see the note on `sortValue`.
      if (left === null && right === null) return 0;
      if (left === null) return 1;
      if (right === null) return -1;
      if (typeof left === 'number' && typeof right === 'number') return (left - right) * factor;
      return String(left).localeCompare(String(right)) * factor;
    });
  }, [rows, columns, sort]);

  const toggleSort = (columnId: string) =>
    setSort((current) =>
      current?.columnId === columnId
        ? current.direction === 'asc'
          ? { columnId, direction: 'desc' }
          : null // third click clears, so a reader can get back to the server's order
        : { columnId, direction: 'asc' },
    );

  if (error) return <>{error}</>;
  if (isLoading) return <LoadingRows columns={columns.length} label={labels.loading} />;
  if (sorted && sorted.length === 0 && empty) return <>{empty}</>;

  const list = sorted ?? [];
  const allKeys = list.map(rowKey);
  const allSelected = selection && allKeys.length > 0 && allKeys.every((k) => selection.selected.has(k));
  const someSelected = selection && allKeys.some((k) => selection.selected.has(k));

  const toggleAll = () => {
    if (!selection) return;
    selection.onChange(allSelected ? new Set() : new Set(allKeys));
  };

  const toggleRow = (key: string) => {
    if (!selection) return;
    const next = new Set(selection.selected);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    selection.onChange(next);
  };

  const cardColumns = columns.filter((c) => !c.hideOnCard);
  const primary = cardColumns.find((c) => c.primary) ?? cardColumns[0];

  return (
    <div className={className}>
      {/* Table, from md up. */}
      <div className="hidden w-full overflow-x-auto rounded-lg border md:block">
        <table className="w-full caption-bottom text-sm">
          {caption ? <caption className="sr-only">{caption}</caption> : null}
          <thead className="bg-muted/50">
            <tr className="border-b">
              {selection ? (
                <th scope="col" className="w-10 px-3">
                  <Checkbox
                    checked={allSelected ? true : someSelected ? 'indeterminate' : false}
                    onCheckedChange={toggleAll}
                    aria-label={labels.selectAll}
                  />
                </th>
              ) : null}
              {columns.map((column) => {
                const active = sort?.columnId === column.id;
                const Icon = !active ? ChevronsUpDown : sort.direction === 'asc' ? ArrowUp : ArrowDown;
                return (
                  <th
                    key={column.id}
                    scope="col"
                    style={column.width ? { width: column.width } : undefined}
                    aria-sort={active ? (sort.direction === 'asc' ? 'ascending' : 'descending') : undefined}
                    className={cn(
                      'h-10 px-3 align-middle text-xs font-medium text-muted-foreground',
                      column.align === 'right' ? 'text-right' : 'text-left',
                    )}
                  >
                    {column.sortValue ? (
                      <button
                        type="button"
                        onClick={() => toggleSort(column.id)}
                        aria-label={labels.sortBy(column.srHeader ?? String(column.header))}
                        className={cn(
                          'inline-flex items-center gap-1 rounded transition-colors hover:text-foreground',
                          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                          column.align === 'right' && 'flex-row-reverse',
                          active && 'text-foreground',
                        )}
                      >
                        {column.header}
                        <Icon className="h-3 w-3 shrink-0" aria-hidden />
                      </button>
                    ) : (
                      <>
                        {column.header}
                        {column.srHeader && !column.header ? (
                          <span className="sr-only">{column.srHeader}</span>
                        ) : null}
                      </>
                    )}
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody className="[&_tr:last-child]:border-0">
            {list.map((row) => {
              const key = rowKey(row);
              return (
                <tr
                  key={key}
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                  className={cn(
                    'border-b transition-colors hover:bg-muted/40',
                    onRowClick && 'cursor-pointer',
                    selection?.selected.has(key) && 'bg-primary/[0.06]',
                    rowClassName?.(row),
                  )}
                >
                  {selection ? (
                    <td className="px-3" onClick={(e) => e.stopPropagation()}>
                      <Checkbox
                        checked={selection.selected.has(key)}
                        onCheckedChange={() => toggleRow(key)}
                        aria-label={labels.selectRow}
                      />
                    </td>
                  ) : null}
                  {columns.map((column) => (
                    <td
                      key={column.id}
                      className={cn(
                        'px-3 py-2.5 align-middle',
                        column.align === 'right' && 'text-right',
                      )}
                    >
                      {column.cell(row)}
                    </td>
                  ))}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      {/* Cards, below md. */}
      <ul className="space-y-2 md:hidden">
        {list.map((row) => {
          const key = rowKey(row);
          const rest = cardColumns.filter((c) => c !== primary);
          return (
            <li key={key}>
              <div
                className={cn(
                  'rounded-lg border bg-card p-3',
                  selection?.selected.has(key) && 'ring-2 ring-primary/40',
                  rowClassName?.(row),
                )}
              >
                <div className="flex items-start gap-2">
                  {selection ? (
                    <Checkbox
                      className="mt-0.5"
                      checked={selection.selected.has(key)}
                      onCheckedChange={() => toggleRow(key)}
                      aria-label={labels.selectRow}
                    />
                  ) : null}
                  <button
                    type="button"
                    disabled={!onRowClick}
                    onClick={onRowClick ? () => onRowClick(row) : undefined}
                    className="min-w-0 flex-1 text-left disabled:cursor-default"
                  >
                    <div className="font-medium">{primary?.cell(row)}</div>
                    <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
                      {rest.map((column) => (
                        <div key={column.id} className="contents">
                          <dt className="text-muted-foreground">
                            {column.srHeader ?? column.header}
                          </dt>
                          <dd className="min-w-0">{column.cell(row)}</dd>
                        </div>
                      ))}
                    </dl>
                  </button>
                </div>
              </div>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

function LoadingRows({ columns, label }: { columns: number; label: string }) {
  return (
    // `role="status"` explicitly: `aria-live` alone does not confer the role, so a screen
    // reader gets the announcement but assistive tooling cannot find the region.
    <div className="rounded-lg border p-3" role="status" aria-busy="true">
      <span className="sr-only">{label}</span>
      {Array.from({ length: 5 }, (_, row) => (
        <div key={row} className="flex gap-3 py-2.5">
          {Array.from({ length: columns }, (_, cell) => (
            <Skeleton key={cell} className="h-4 flex-1" />
          ))}
        </div>
      ))}
    </div>
  );
}
