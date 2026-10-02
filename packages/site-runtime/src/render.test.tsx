import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { BUILTIN_COMPONENTS, builtinRegistry, defaultProps } from './components';
import type { Node } from './document';
import { RenderNode } from './render';
import { RenderModeContext, type RenderMode } from './renderMode';

function html(node: Node, mode: RenderMode = 'live', registry = builtinRegistry): string {
  return renderToStaticMarkup(
    <RenderModeContext.Provider value={mode}>
      <RenderNode node={node} registry={registry} />
    </RenderModeContext.Provider>,
  );
}

const page: Node = {
  id: 'root',
  type: 'dcms.page',
  slots: {
    default: [
      {
        id: 's1',
        type: 'dcms.stack',
        props: { direction: 'horizontal', gap: 'lg' },
        slots: { default: [{ id: 'h1', type: 'dcms.heading', props: { text: 'Hi <there>', level: '1' } }] },
      },
    ],
  },
};

describe('RenderNode', () => {
  it('wraps every node and every slot in the shared DOM shape', () => {
    expect(html(page)).toBe(
      '<div class="dcms-node" data-dcms-node="root" data-dcms-type="dcms.page"><main class="dcms-page">' +
        '<span style="display:contents"><div class="dcms-slot" data-dcms-slot="default">' +
        '<div class="dcms-node" data-dcms-node="s1" data-dcms-type="dcms.stack">' +
        '<span style="display:contents"><div class="dcms-slot dcms-stack dcms-stack-horizontal dcms-gap-lg dcms-align-stretch dcms-justify-start" data-dcms-slot="default">' +
        '<div class="dcms-node" data-dcms-node="h1" data-dcms-type="dcms.heading"><h1 class="dcms-heading dcms-heading-1 dcms-text-start">Hi &lt;there&gt;</h1></div>' +
        '</div></span></div></div></span></main></div>',
    );
  });

  it('falls back to the default for a value no option allows, so it never reaches a class', () => {
    const out = html({ id: 'h', type: 'dcms.heading', props: { level: '7" onload="x', align: 'evil' } });
    expect(out).toContain('<h2 class="dcms-heading dcms-heading-2 dcms-text-start">');
  });

  it('turns a navigate button into a link on the site and an inert button on the canvas', () => {
    const button: Node = { id: 'b', type: 'dcms.button', props: { label: 'Go' }, action: { type: 'navigate', to: '/contact' } };
    expect(html(button, 'live')).toContain('<a class="dcms-button dcms-button-primary dcms-button-md" href="/contact">Go</a>');
    expect(html(button, 'edit')).toContain('<button type="button" class="dcms-button dcms-button-primary dcms-button-md">Go</button>');
  });

  it('opens an external link in a new tab without handing it the opener', () => {
    const button: Node = { id: 'b', type: 'dcms.button', action: { type: 'open-external', href: 'https://x.test', newTab: true } };
    expect(html(button)).toContain('target="_blank" rel="noopener noreferrer"');
  });

  it('shows an empty image to the editor and nothing to visitors', () => {
    const image: Node = { id: 'i', type: 'dcms.image' };
    expect(html(image, 'edit')).toContain('Choose an image');
    expect(html(image, 'live')).not.toContain('<img');
  });

  it('names an unknown component in the editor and hides it on the site', () => {
    const ghost: Node = { id: 'g', type: 'dcms.ghost' };
    expect(html(ghost, 'edit')).toContain('Unknown component “dcms.ghost”');
    expect(html(ghost, 'live')).toBe('<div class="dcms-node" data-dcms-node="g" data-dcms-type="dcms.ghost"></div>');
  });

});

describe('BUILTIN_COMPONENTS', () => {
  it('renders every built-in with its defaults, in both modes', () => {
    for (const def of BUILTIN_COMPONENTS) {
      const node: Node = { id: 'n', type: def.type, props: defaultProps(def) };
      for (const mode of ['edit', 'live'] as const) expect(() => html(node, mode)).not.toThrow();
    }
  });

  it('keeps the page root out of every slot', () => {
    expect(builtinRegistry.get('dcms.page')?.draggable).toBe(false);
  });
});
