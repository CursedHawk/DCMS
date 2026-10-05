import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { builtinRegistry } from './components';
import type { Node } from './document';
import { RenderNode } from './render';
import { RenderModeContext } from './renderMode';

const html = (node: Node, mode: 'live' | 'edit' = 'live') =>
  renderToStaticMarkup(
    <RenderModeContext.Provider value={mode}>
      <RenderNode node={node} registry={builtinRegistry} />
    </RenderModeContext.Provider>,
  );

describe('content primitives', () => {
  it('an icon draws from the embedded set, decorative unless it is given a meaning', () => {
    expect(html({ id: 'i', type: 'dcms.icon', props: { icon: 'phone' } })).toMatch(/<svg[^>]*aria-hidden="true"[^>]*><path d=/);
    expect(html({ id: 'i', type: 'dcms.icon', props: { icon: 'phone', label: 'Call us' } })).toContain('role="img" aria-label="Call us"');
    // A name outside the set falls back rather than drawing nothing or anything else.
    expect(html({ id: 'i', type: 'dcms.icon', props: { icon: '<script>' } })).toContain('<svg');
  });

  it('a list takes one point per line, with ticks by default and numbers as an ordered list', () => {
    const ticks = html({ id: 'l', type: 'dcms.list', props: { items: 'One\n\n Two ' } });
    expect(ticks).toContain('<ul class="dcms-list dcms-list-check');
    expect(ticks.match(/<li>/g)).toHaveLength(2);
    expect(ticks).toContain('<span>Two</span>');
    expect(html({ id: 'l', type: 'dcms.list', props: { items: 'A\nB', marker: 'number' } })).toMatch(/^.*<ol class="dcms-list dcms-list-number/);
  });

  it('a card links as a whole on the site, never on the canvas, and keeps its slots', () => {
    const card: Node = {
      id: 'c',
      type: 'dcms.card',
      props: { linkLabel: 'Read about the gala' },
      action: { type: 'navigate', to: '/gala' },
      slots: { default: [{ id: 'h', type: 'dcms.heading', props: { text: 'Gala' } }] },
    };
    expect(html(card)).toContain('<a href="/gala" class="dcms-card-cover" aria-label="Read about the gala"></a>');
    expect(html(card, 'edit')).not.toContain('dcms-card-cover');
    expect(html(card)).toContain('Gala</h2>');
  });

  it('a quote and a badge render what they are given', () => {
    expect(html({ id: 'q', type: 'dcms.quote', props: { text: 'Superb', cite: 'Ada' } })).toContain('<blockquote><p>Superb</p></blockquote><figcaption>Ada</figcaption>');
    expect(html({ id: 'b', type: 'dcms.badge', props: { text: 'Sold out', tone: 'danger' } })).toContain('<span class="dcms-badge dcms-badge-danger">Sold out</span>');
  });
});
