import { describe, expect, it } from 'vitest';
import type { AppConfig } from '../../api';
import { callAt, contextFor, hoverFor, suggestionsAt } from './expressionLanguage';

const config = {
  tables: [
    {
      id: 't-deal', apiName: 'deal', displayName: 'Deal',
      fields: [
        { id: 'f1', apiName: 'amount', displayName: 'Amount', type: 'decimal' },
        { id: 'f2', apiName: 'status', displayName: 'Status', type: 'choice' },
      ],
    },
    { id: 't-co', apiName: 'company', displayName: 'Company', fields: [] },
  ],
  relationships: [
    { id: 'r1', apiName: 'company', kind: 'manyToOne', sourceTableId: 't-deal', targetTableId: 't-co' },
    { id: 'r2', apiName: 'tags', kind: 'manyToMany', sourceTableId: 't-deal', targetTableId: 't-co' },
  ],
} as unknown as AppConfig;

const language = {
  functions: [
    { name: 'len', signature: 'len(value)', description: 'Length.', minArgs: 1, maxArgs: 1, returns: 'number' },
    { name: 'round', signature: 'round(number, digits?)', description: 'Rounds.', minArgs: 1, maxArgs: 2, returns: 'number | null' },
    { name: 'now', signature: 'now()', description: 'Now.', minArgs: 0, maxArgs: 0, returns: 'string' },
  ],
  variables: ['event', 'row', 'previous', 'changedFields', 'input', 'steps', 'run'],
};

const updated = contextFor(config, { event: 'row.updated', tableId: 't-deal' }, ['find_owner'], language);
const labels = (before: string, ctx = updated) => suggestionsAt(before, ctx).items.map((i) => i.label);

describe('the flow expression editor', () => {
  it('offers the trigger table’s fields and lookups after row., but not many-to-many sets', () => {
    expect(labels('row.')).toEqual(expect.arrayContaining(['amount', 'status', 'company', 'id', 'created_at']));
    expect(labels('row.')).not.toContain('tags');
    expect(suggestionsAt('row.am', updated).from).toBe(4);
  });

  it('knows that only row.updated has previous values, and no row exists for other triggers', () => {
    expect(labels('previous.')).toEqual(expect.arrayContaining(['amount', 'status']));
    expect(labels('previous.')).not.toContain('id');
    const created = contextFor(config, { event: 'row.created', tableId: 't-deal' }, [], language);
    expect(labels('previous.', created)).toEqual([]);
    const manual = contextFor(config, { event: 'manual', tableId: 't-deal' }, [], language);
    expect(labels('row.', manual)).toEqual([]);
  });

  it('offers the earlier steps, the run and the event envelope', () => {
    expect(labels('steps.')).toEqual(['find_owner']);
    expect(labels('run.')).toEqual(['id', 'flow', 'revision', 'depth']);
    expect(labels('event.')).toContain('eventName');
  });

  it('offers variables, functions and keywords for a bare word, and nothing deeper than a root’s members or inside a string', () => {
    const top = labels('row.amount > 1 && le');
    expect(top).toEqual(expect.arrayContaining(['row', 'steps', 'len', 'round', 'now', 'in', 'null']));
    expect(suggestionsAt('row.amount > 1 && le', updated).from).toBe('row.amount > 1 && '.length);
    expect(labels('row.company.')).toEqual([]);
    expect(labels("row.status == 'row.")).toEqual([]);
  });

  it('inserts a function with its parentheses', () => {
    const items = suggestionsAt('', updated).items;
    expect(items.find((i) => i.label === 'len')).toMatchObject({ insertText: 'len($1)', snippet: true });
    expect(items.find((i) => i.label === 'now')).toMatchObject({ insertText: 'now()' });
  });

  it('finds the call and argument the cursor is in, past nested calls and strings', () => {
    expect(callAt('round(')).toEqual({ name: 'round', argument: 0 });
    expect(callAt('round(len(row.x), ')).toEqual({ name: 'round', argument: 1 });
    expect(callAt("round(len('a,b'")).toEqual({ name: 'len', argument: 0 });
    expect(callAt('round(1, 2)')).toBeNull();
    expect(callAt("'round(")).toBeNull();
  });

  it('explains functions, variables and fields on hover', () => {
    expect(hoverFor('round', '', updated)).toContain('round(number, digits?)');
    expect(hoverFor('steps', '', updated)).toContain('outputs');
    expect(hoverFor('amount', 'row.', updated)).toContain('number | null');
    expect(hoverFor('nothing', '', updated)).toBeNull();
  });
});
