import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { BUILTIN_COMPONENTS, builtinRegistry, defaultProps } from './components';
import { nodeSchema, type Node } from './document';
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
    expect(html(button, 'live')).toContain('<a href="/contact" class="dcms-button dcms-button-primary dcms-button-md">Go</a>');
    // No href on the canvas: a click there selects, and must not navigate the editor's frame.
    expect(html(button, 'edit')).toContain('<a class="dcms-button dcms-button-primary dcms-button-md">Go</a>');
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

describe('responsive props', () => {
  it('adds a tablet and a mobile class beside the desktop one', () => {
    const grid: Node = {
      id: 'g',
      type: 'dcms.grid',
      props: { columns: '4', gap: 'lg' },
      responsive: { tablet: { columns: '2' }, mobile: { columns: '1', gap: 'sm' } },
    };
    expect(html(grid)).toContain('class="dcms-slot dcms-grid dcms-cols-4 t-dcms-cols-2 m-dcms-cols-1 dcms-gap-lg m-dcms-gap-sm"');
  });

  it('keeps an override to the prop’s own options', () => {
    const grid: Node = { id: 'g', type: 'dcms.grid', responsive: { mobile: { columns: '99"><script>' } } };
    expect(html(grid)).toContain('dcms-cols-3 m-dcms-cols-3');
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

describe('an instance’s own CSS', () => {
  it('is scoped to the component’s element, and anything that could escape that scope is refused', () => {
    const node = { id: 'h', type: 'dcms.heading', props: { text: 'Hi' }, css: 'letter-spacing: .2em' };
    expect(renderToStaticMarkup(<RenderNode node={node} registry={builtinRegistry} />)).toContain(
      '<style>[data-dcms-node="h"] > :not(style) { letter-spacing: .2em }</style>',
    );
    for (const css of ['color: red } body { display: none', '</style><script>', '@import "x"', 'background: url(https://evil)', 'c\\6f lor: red']) {
      expect(nodeSchema.safeParse({ ...node, css }).success, css).toBe(false);
      expect(renderToStaticMarkup(<RenderNode node={{ ...node, css }} registry={builtinRegistry} />)).not.toContain('<style>');
    }
  });
});
