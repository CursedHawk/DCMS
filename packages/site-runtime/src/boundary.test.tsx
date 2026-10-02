// @vitest-environment jsdom
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { describe, expect, it, vi } from 'vitest';
import { BUILTIN_COMPONENTS } from './components';
import type { Node } from './document';
import { createRegistry } from './registry';
import { RenderNode } from './render';
import { RenderModeContext, type RenderMode } from './renderMode';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const registry = createRegistry([
  ...BUILTIN_COMPONENTS,
  {
    type: 'test.boom',
    version: 1,
    label: 'Boom',
    category: 'test',
    props: [],
    component: () => {
      throw new Error('boom');
    },
  },
]);

const tree: Node = {
  id: 'root',
  type: 'dcms.page',
  slots: { default: [{ id: 'x', type: 'test.boom' }, { id: 't', type: 'dcms.text', props: { text: 'still here' } }] },
};

function mount(mode: RenderMode): HTMLElement {
  const el = document.createElement('div');
  act(() => {
    createRoot(el).render(
      <RenderModeContext.Provider value={mode}>
        <RenderNode node={tree} registry={registry} />
      </RenderModeContext.Provider>,
    );
  });
  return el;
}

describe('NodeBoundary', () => {
  it('keeps a component that throws to its own node, on the site and in the editor', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const live = mount('live');
    expect(live.textContent).toBe('still here');
    expect(live.querySelector('[data-dcms-node="x"]')?.innerHTML).toBe('');

    const edit = mount('edit');
    expect(edit.querySelector('[data-dcms-node="x"] [role="alert"]')?.textContent).toBe('“test.boom” could not be drawn');
    expect(edit.textContent).toContain('still here');
    spy.mockRestore();
  });
});
