// @vitest-environment jsdom
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { BUILTIN_COMPONENTS, builtinRegistry } from './components';
import type { Node } from './document';
import { timeLeft } from './interactiveComponents';
import type { StarterNode } from './registry';
import { RenderNode } from './render';
import { RenderModeContext } from './renderMode';
import { checkVisualSite } from './validate';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const html = (node: Node, mode: 'live' | 'edit' = 'live') =>
  renderToStaticMarkup(
    <RenderModeContext.Provider value={mode}>
      <RenderNode node={node} registry={builtinRegistry} />
    </RenderModeContext.Provider>,
  );

let n = 0;
const fromStarter = (s: StarterNode): Node => ({
  id: `s${n++}`,
  type: s.type,
  ...(s.props ? { props: s.props } : {}),
  ...(s.slots ? { slots: Object.fromEntries(Object.entries(s.slots).map(([k, v]) => [k, v.map(fromStarter)])) } : {}),
});

describe('starters', () => {
  it('every component that starts with children starts with a tree the validator accepts', () => {
    const json = (v: unknown) => `${JSON.stringify(v)}\n`;
    for (const def of BUILTIN_COMPONENTS.filter((d) => d.starter)) {
      const node: Node = { id: 'x', type: def.type, slots: Object.fromEntries(Object.entries(def.starter!).map(([k, v]) => [k, v.map(fromStarter)])) };
      // Placed where it may go: an item component inside its own parent.
      const placed: Node = def.allowedParents?.length
        ? { id: 'p', type: def.allowedParents[0]!, slots: { [builtinRegistry.get(def.allowedParents[0]!)!.slots![0]!.name]: [node] } }
        : node;
      const problems = checkVisualSite({
        'dcms/app.json': json({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }),
        'dcms/pages/home.json': json({ schemaVersion: 1, id: 'home', title: 'Home', root: { id: 'r', type: 'dcms.page', slots: { default: [placed] } } }),
      });
      // The component's own required settings (a form's plugin) are the author's to fill in; what
      // is checked here is the children it starts with.
      expect(problems.filter((p) => !p.message.endsWith(' is required.')), def.type).toEqual([]);
    }
  });
});

describe('accordion', () => {
  it('is native details: answers closed on the site, open on the canvas', () => {
    const node: Node = { id: 'a', type: 'dcms.accordion', slots: { items: [{ id: 'q', type: 'dcms.accordion-item', props: { title: 'Why?' } }] } };
    expect(html(node)).toMatch(/<details class="dcms-accordion-item"><summary><span>Why\?<\/span>/);
    expect(html(node, 'edit')).toContain('<details class="dcms-accordion-item" open="">');
  });
});

describe('tabs', () => {
  it('builds its tab bar from the panels and shows one at a time; arrows move between tabs', async () => {
    const node: Node = {
      id: 't',
      type: 'dcms.tabs',
      slots: {
        tabs: [
          { id: 'a', type: 'dcms.tab', props: { label: 'One' }, slots: { default: [{ id: 'x', type: 'dcms.text', props: { text: 'first' } }] } },
          { id: 'b', type: 'dcms.tab', props: { label: 'Two' }, slots: { default: [{ id: 'y', type: 'dcms.text', props: { text: 'second' } }] } },
        ],
      },
    };
    const el = document.createElement('div');
    document.body.appendChild(el);
    await act(async () => createRoot(el).render(<RenderNode node={node} registry={builtinRegistry} />));
    await act(async () => undefined);
    const tabs = [...el.querySelectorAll('[role="tab"]')];
    expect(tabs.map((t) => t.textContent)).toEqual(['One', 'Two']);
    const panels = [...el.querySelectorAll<HTMLElement>('[data-dcms-tab]')];
    expect(panels.map((p) => p.hidden)).toEqual([false, true]);
    await act(async () => tabs[0]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true })));
    expect(panels.map((p) => p.hidden)).toEqual([true, false]);
  });
});

describe('countdown', () => {
  it('splits what is left into days, hours, minutes and seconds, and stops at zero', () => {
    const now = Date.parse('2026-10-05T10:00:00Z');
    expect(timeLeft('2026-10-07T12:30:15Z', now)).toEqual({ days: 2, hours: 2, minutes: 30, seconds: 15 });
    expect(timeLeft('2026-10-05T09:00:00Z', now)).toBeNull();
    expect(timeLeft('not a date', now)).toBeNull();
  });
});

describe('social links', () => {
  it('shows only the profiles given, drops unsafe links, and turns an address into mailto', () => {
    const out = html({ id: 's', type: 'dcms.social', props: { instagram: 'https://instagram.com/acme', facebook: 'javascript:alert(1)', email: 'hi@acme.test' } });
    expect(out).toContain('href="https://instagram.com/acme"');
    expect(out).toContain('href="mailto:hi@acme.test"');
    expect(out).not.toContain('javascript');
    expect(out.match(/<li>/g)).toHaveLength(2);
  });
});
