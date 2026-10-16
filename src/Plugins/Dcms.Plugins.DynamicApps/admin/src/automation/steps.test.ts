import { describe, expect, it } from 'vitest';
import type { FlowStep } from '../api';
import { inputSchemaFor, SCRIPT_TEMPLATE, toDraft, toStep, withAction } from './steps';

describe('a flow step as its card edits it', () => {
  it('round-trips an ordinary step unchanged', () => {
    const step: FlowStep = { id: 'notify', action: 'dcms.notifications.raise@1', input: { title: 'Big {{ row.title }}', body: '{{ row.id }}' }, condition: 'row.amount > 1' };
    expect(toStep(toDraft(step))).toEqual(step);
    const plain: FlowStep = { id: 'q', action: 'records.query@1', input: { table: 'deals' } };
    expect(toStep(toDraft(plain))).toEqual(plain);
    expect(toStep(toDraft(plain))).not.toHaveProperty('condition');
  });

  it('holds a script’s code apart from the rest of its input, and puts it back', () => {
    const step: FlowStep = { id: 'calc', action: 'script.run@1', input: { code: 'export default () => "{{ x }}"', language: 'ts', args: { n: '{{ input.n }}' } } };
    const draft = toDraft(step);
    expect(draft.code).toBe('export default () => "{{ x }}"');
    expect(JSON.parse(draft.inputText)).toEqual({ language: 'ts', args: { n: '{{ input.n }}' } });
    expect(toStep(draft)).toEqual(step);
  });

  it('is not a step while its input is not a JSON object', () => {
    const draft = toDraft({ id: 'a', action: 'records.query@1', input: {} });
    expect(toStep({ ...draft, inputText: '{ "table": ' })).toBeNull();
    expect(toStep({ ...draft, inputText: '[1]' })).toBeNull();
    expect(toStep({ ...draft, inputText: '' })).toEqual({ id: 'a', action: 'records.query@1', input: {} });
  });

  it('gives a step that becomes a script a starting function, and drops it when it stops being one', () => {
    const draft = withAction(toDraft({ id: 'a', action: 'records.query@1', input: { table: 'deals' } }), 'script.run@1');
    expect(draft.code).toBe(SCRIPT_TEMPLATE);
    expect(toStep(draft)!.input).toEqual({ language: 'ts', args: {}, code: SCRIPT_TEMPLATE });
    expect(withAction(draft, 'records.query@1')).toMatchObject({ code: null, inputText: '{}' });
  });

  it('checks a script’s JSON input without its code', () => {
    const action = { key: 'script.run@1', id: 'script.run', major: 1, description: '', risk: 'read' as const,
      inputSchema: { type: 'object', required: ['code'], properties: { code: { type: 'string' }, args: { type: 'object' } } } };
    expect(inputSchemaFor(action, true)).toEqual({ type: 'object', required: [], properties: { args: { type: 'object' } } });
    expect(inputSchemaFor(action, false)).toBe(action.inputSchema);
  });
});
