import { builtinRegistry, tenantComponentSchema, type TenantComponentDoc } from '@dcms/site-runtime';
import { describe, expect, it } from 'vitest';
import { editSetting, editSlot, exposeProp, exposeSlot, moveSetting, templateParts, templateSlots, unexpose, unexposeSlot } from './model';

const doc: TenantComponentDoc = {
  schemaVersion: 1,
  name: 'card',
  version: 1,
  label: 'Card',
  props: [],
  slots: [],
  bindings: {},
  slotTargets: {},
  root: {
    id: 'root',
    type: 'dcms.stack',
    slots: { default: [{ id: 't', type: 'dcms.heading', props: { text: 'Title' } }, { id: 'box', type: 'dcms.stack', slots: { default: [] } }] },
  },
};
const heading = builtinRegistry.get('dcms.heading')!;

describe('the studio model', () => {
  it('exposes a setting at its current value, edits and orders it, and takes it back', () => {
    let d = exposeProp(doc, 't', heading, heading.props.find((p) => p.name === 'text')!, 'Title');
    d = exposeProp(d, 't', heading, heading.props.find((p) => p.name === 'level')!, '2');
    expect(d.props.map((p) => p.name)).toEqual(['text', 'level']);
    expect(d.props[0]).toMatchObject({ label: 'Heading: Text', default: 'Title' });
    d = moveSetting(editSetting(d, 'level', { label: 'Size', description: 'How big' }), 'level', -1);
    expect(d.props.map((p) => p.label)).toEqual(['Size', 'Heading: Text']);
    expect(tenantComponentSchema.safeParse(d).success).toBe(true);
    expect(unexpose(d, 'text').bindings).toEqual({ level: [{ node: 't', prop: 'level' }] });
  });

  it('offers only empty areas as slots, and limits what goes in them', () => {
    expect(templateSlots(doc, builtinRegistry).map((s) => [s.part.node.id, s.empty])).toEqual([['root', false], ['box', true]]);
    let d = exposeSlot(doc, 'box', 'default', 'Content');
    d = editSlot(d, 'content', { allowed: ['dcms.text'], max: 2 });
    expect(d.slots).toEqual([{ name: 'content', label: 'Content', allowed: ['dcms.text'], max: 2 }]);
    expect(editSlot(d, 'content', { allowed: [], max: undefined }).slots).toEqual([{ name: 'content', label: 'Content' }]);
    expect(tenantComponentSchema.safeParse(d).success).toBe(true);
    expect(unexposeSlot(d, 'content').slotTargets).toEqual({});
  });

  it('names parts so two of a kind can be told apart', () => {
    expect(templateParts(doc, builtinRegistry).map((p) => p.title)).toEqual(['Stack', 'Heading “Title”', 'Stack']);
  });
});
