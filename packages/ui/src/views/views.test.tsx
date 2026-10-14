import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { useState } from 'react';
import { user } from '../test/user';
import { FilterBuilder } from './FilterBuilder';
import { cleanFilter, countConditions, sameDefinition, withOp, type ViewField, type ViewFilter } from './model';

const fields: ViewField[] = [
  { key: 'title', label: 'Title', kind: 'text' },
  { key: 'price', label: 'Price', kind: 'number' },
  { key: 'tags', label: 'Tags', kind: 'tags' },
  { key: 'internal', label: 'Internal', kind: 'text', filterable: false },
];

describe('view model', () => {
  it('drops half-made conditions and the groups they leave empty', () => {
    const f: ViewFilter = {
      or: [
        { field: 'title', op: 'contains', value: '' },
        { and: [{ field: 'tags', op: 'in', value: [] }] },
        { field: 'price', op: 'gte', value: 10 },
        { field: 'title', op: 'isNull', value: true },
      ],
    };
    expect(cleanFilter(f)).toEqual({
      or: [
        { field: 'price', op: 'gte', value: 10 },
        { field: 'title', op: 'isNull', value: true },
      ],
    });
    expect(cleanFilter({ and: [{ field: 'title', op: 'eq' }] })).toBeUndefined();
    expect(countConditions(f)).toBe(4);
  });

  it('keeps the value when the operator changes between one and many', () => {
    expect(withOp({ field: 'tags', op: 'contains', value: 'a' }, 'in')).toEqual({ field: 'tags', op: 'in', value: ['a'] });
    expect(withOp({ field: 'tags', op: 'in', value: ['a', 'b'] }, 'contains')).toEqual({ field: 'tags', op: 'contains', value: 'a' });
    expect(withOp({ field: 'title', op: 'eq', value: 'x' }, 'isNotEmpty')).toEqual({ field: 'title', op: 'isNull', value: false });
  });

  it('compares definitions by what they show', () => {
    const a = { columns: ['title'], sort: [], filter: { and: [{ field: 'title', op: 'eq' as const, value: '' }] } };
    expect(sameDefinition(a, { columns: ['title'], sort: [], filter: null })).toBe(true);
    expect(sameDefinition(a, { columns: ['price'], sort: [], filter: null })).toBe(false);
  });
});

function Builder({ onChange }: { onChange: (f: ViewFilter) => void }) {
  const [value, setValue] = useState<ViewFilter>({ and: [] });
  return (
    <FilterBuilder
      fields={fields}
      value={value}
      onChange={(next) => {
        setValue(next);
        onChange(next);
      }}
    />
  );
}

// The field and operator lists and the column chooser are Radix overlays, which jsdom cannot
// settle (see test/user.ts); the e2e suite opens them.
describe('FilterBuilder', () => {
  it('builds a nested any/all filter from the fields it may filter', async () => {
    const onChange = vi.fn();
    render(<Builder onChange={onChange} />);

    await user.click(screen.getByRole('button', { name: 'Add condition' }));
    await user.type(screen.getByRole('textbox', { name: 'Value' }), 'sale');
    await user.click(screen.getByRole('button', { name: 'Add group' }));

    expect(onChange).toHaveBeenLastCalledWith({
      and: [{ field: 'title', op: 'contains', value: 'sale' }, { or: [{ field: 'title', op: 'contains' }] }],
    });
  });

  it('removes a condition', async () => {
    const onChange = vi.fn();
    render(<Builder onChange={onChange} />);
    await user.click(screen.getByRole('button', { name: 'Add condition' }));
    await user.click(screen.getByRole('button', { name: 'Remove condition' }));
    expect(onChange).toHaveBeenLastCalledWith({ and: [] });
  });
});
