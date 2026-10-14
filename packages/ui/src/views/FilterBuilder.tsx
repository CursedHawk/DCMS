import { Plus, Trash2 } from 'lucide-react';
import { cn } from '../cn';
import { Button } from '../ui/button';
import { Checkbox } from '../ui/checkbox';
import { Input } from '../ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '../ui/select';
import { TagsInput } from '../ui/tags-input';
import { DEFAULT_VIEW_LABELS, type ViewLabels } from './labels';
import {
  groupOf,
  isCondition,
  makeGroup,
  newCondition,
  opsFor,
  uiOpOf,
  withOp,
  type ViewCondition,
  type ViewField,
  type ViewFilter,
} from './model';

/**
 * Edits a view's filter (#278): conditions in groups that match all or any of them, groups in
 * groups. Every field the table can filter is offered, each with the operators its kind takes.
 * Half-made conditions stay in the editor; `cleanFilter` drops them before the filter is used.
 */
export function FilterBuilder({
  fields,
  value,
  onChange,
  labels: partial,
  maxDepth = 3,
}: {
  fields: readonly ViewField[];
  value: ViewFilter | null | undefined;
  onChange: (next: ViewFilter) => void;
  labels?: Partial<ViewLabels>;
  /** How deep groups nest; the server allows five levels. */
  maxDepth?: number;
}) {
  const labels = { ...DEFAULT_VIEW_LABELS, ...partial };
  const usable = fields.filter((f) => f.filterable !== false);
  const root = value && !isCondition(value) && !('not' in value) ? value : makeGroup('and', value ? [value] : []);
  return (
    <Group
      group={root}
      onChange={onChange}
      fields={usable}
      labels={labels}
      depth={1}
      maxDepth={maxDepth}
    />
  );
}

function Group({
  group,
  onChange,
  onRemove,
  fields,
  labels,
  depth,
  maxDepth,
}: {
  group: ViewFilter;
  onChange: (next: ViewFilter) => void;
  onRemove?: () => void;
  fields: readonly ViewField[];
  labels: ViewLabels;
  depth: number;
  maxDepth: number;
}) {
  const { mode, items } = groupOf(group);
  const set = (next: ViewFilter[]) => onChange(makeGroup(mode, next));
  const replace = (index: number, next: ViewFilter) => set(items.map((x, i) => (i === index ? next : x)));
  const remove = (index: number) => set(items.filter((_, i) => i !== index));

  return (
    <div
      role="group"
      aria-label={`${labels.matchPrefix} ${mode === 'and' ? labels.matchAll : labels.matchAny}`}
      className={cn('min-w-0 space-y-2', depth > 1 && 'rounded-md border border-dashed p-2')}
    >
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <span>{labels.matchPrefix}</span>
        <Select value={mode} onValueChange={(m) => onChange(makeGroup(m as 'and' | 'or', items))}>
          <SelectTrigger className="h-8 w-24" aria-label={labels.matchPrefix}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="and">{labels.matchAll}</SelectItem>
            <SelectItem value="or">{labels.matchAny}</SelectItem>
          </SelectContent>
        </Select>
        {onRemove && (
          <Button size="icon" variant="ghost" className="ml-auto h-8 w-8" aria-label={labels.removeGroup} onClick={onRemove}>
            <Trash2 className="h-4 w-4" />
          </Button>
        )}
      </div>

      {items.length === 0 && <p className="text-sm text-muted-foreground">{labels.noConditions}</p>}
      {items.map((item, index) =>
        isCondition(item) ? (
          <Condition
            key={index}
            condition={item}
            fields={fields}
            labels={labels}
            onChange={(next) => replace(index, next)}
            onRemove={() => remove(index)}
          />
        ) : (
          <Group
            key={index}
            group={item}
            fields={fields}
            labels={labels}
            depth={depth + 1}
            maxDepth={maxDepth}
            onChange={(next) => replace(index, next)}
            onRemove={() => remove(index)}
          />
        ),
      )}

      <div className="flex flex-wrap gap-2">
        <Button size="sm" variant="outline" disabled={fields.length === 0} onClick={() => set([...items, newCondition(fields[0])])}>
          <Plus className="h-3.5 w-3.5" /> {labels.addCondition}
        </Button>
        {depth < maxDepth && (
          <Button
            size="sm"
            variant="ghost"
            disabled={fields.length === 0}
            onClick={() => set([...items, makeGroup(mode === 'and' ? 'or' : 'and', [newCondition(fields[0])])])}
          >
            <Plus className="h-3.5 w-3.5" /> {labels.addGroup}
          </Button>
        )}
      </div>
    </div>
  );
}

function Condition({
  condition,
  onChange,
  onRemove,
  fields,
  labels,
}: {
  condition: ViewCondition;
  onChange: (next: ViewCondition) => void;
  onRemove: () => void;
  fields: readonly ViewField[];
  labels: ViewLabels;
}) {
  const field = fields.find((f) => f.key === condition.field);
  const op = uiOpOf(condition);
  return (
    <div className="grid grid-cols-1 items-start gap-2 sm:grid-cols-[minmax(8rem,1fr)_minmax(8rem,auto)_minmax(8rem,1.5fr)_auto]">
      <Select
        value={condition.field}
        onValueChange={(key) => {
          const next = fields.find((f) => f.key === key);
          if (next) onChange(newCondition(next));
        }}
      >
        <SelectTrigger className="h-8 min-w-0" aria-label={labels.field}>
          <SelectValue placeholder={condition.field} />
        </SelectTrigger>
        <SelectContent>
          {fields.map((f) => (
            <SelectItem key={f.key} value={f.key}>
              {f.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
      <Select value={op} onValueChange={(o) => onChange(withOp(condition, o as typeof op))}>
        <SelectTrigger className="h-8 min-w-0" aria-label={labels.operator}>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {(field ? opsFor(field.kind) : [op]).map((o) => (
            <SelectItem key={o} value={o}>
              {labels.op[o]}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
      <div className="min-w-0">
        {field && op !== 'isEmpty' && op !== 'isNotEmpty' && (
          <ValueInput field={field} condition={condition} labels={labels} onChange={(value) => onChange({ ...condition, value })} />
        )}
      </div>
      <Button size="icon" variant="ghost" className="h-8 w-8 justify-self-end" aria-label={labels.removeCondition} onClick={onRemove}>
        <Trash2 className="h-4 w-4" />
      </Button>
    </div>
  );
}

function ValueInput({
  field,
  condition,
  labels,
  onChange,
}: {
  field: ViewField;
  condition: ViewCondition;
  labels: ViewLabels;
  onChange: (value: unknown) => void;
}) {
  const list = condition.op === 'in' || condition.op === 'nin';
  const value = condition.value;

  if (field.kind === 'boolean') {
    return (
      <Select value={value === false ? 'false' : 'true'} onValueChange={(v) => onChange(v === 'true')}>
        <SelectTrigger className="h-8" aria-label={labels.value}>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="true">{labels.yes}</SelectItem>
          <SelectItem value="false">{labels.no}</SelectItem>
        </SelectContent>
      </Select>
    );
  }
  if (field.kind === 'choice' && field.options) {
    if (list) {
      const picked = Array.isArray(value) ? (value as string[]) : [];
      return (
        <fieldset className="flex flex-wrap gap-x-3 gap-y-1" aria-label={labels.value}>
          {field.options.map((o) => (
            <label key={o.value} className="flex items-center gap-1.5 text-sm">
              <Checkbox
                checked={picked.includes(o.value)}
                onCheckedChange={(on) => onChange(on === true ? [...picked, o.value] : picked.filter((v) => v !== o.value))}
              />
              {o.label}
            </label>
          ))}
        </fieldset>
      );
    }
    return (
      <Select value={typeof value === 'string' ? value : undefined} onValueChange={onChange}>
        <SelectTrigger className="h-8" aria-label={labels.value}>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {field.options.map((o) => (
            <SelectItem key={o.value} value={o.value}>
              {o.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    );
  }
  if (list) {
    return <TagsInput value={Array.isArray(value) ? (value as string[]).map(String) : []} onChange={onChange} placeholder={labels.value} />;
  }
  if (field.kind === 'number') {
    return (
      <Input
        type="number"
        className="h-8"
        aria-label={labels.value}
        value={typeof value === 'number' ? value : ''}
        onChange={(e) => onChange(e.target.value === '' ? undefined : Number(e.target.value))}
      />
    );
  }
  return (
    <Input
      type={field.kind === 'date' ? 'date' : 'text'}
      className="h-8"
      aria-label={labels.value}
      value={typeof value === 'string' ? value : ''}
      onChange={(e) => onChange(e.target.value)}
    />
  );
}
