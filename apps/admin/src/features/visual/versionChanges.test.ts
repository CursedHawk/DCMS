import type { TenantComponentDoc } from '@dcms/site-runtime';
import { describe, expect, it } from 'vitest';
import { versionChanges } from './documents';

const doc = (version: number, props: string[], slots: string[]): TenantComponentDoc =>
  ({
    schemaVersion: 1,
    name: 'card',
    version,
    label: 'Card',
    props: props.map((name) => ({ kind: 'text', name, label: name })),
    slots: slots.map((name) => ({ name })),
    bindings: {},
    slotTargets: {},
    root: { id: 'r', type: 'dcms.stack' },
  }) as never;

describe('what updating an instance changes', () => {
  it('names settings that go (with their value), settings that arrive, and content that stops showing', () => {
    const node = {
      id: 'c',
      type: 'tenant.card',
      version: 1,
      props: { title: 'Gala', badge: 'New' },
      slots: { extras: [{ id: 'x', type: 'dcms.text' }], empty: [] },
    };
    expect(versionChanges(node, doc(1, ['title', 'badge'], ['extras', 'empty']), doc(2, ['title', 'subtitle'], []))).toEqual({
      removed: [{ name: 'badge', value: 'New' }],
      added: ['subtitle'],
      hiddenSlots: ['extras'],
    });
  });
});
