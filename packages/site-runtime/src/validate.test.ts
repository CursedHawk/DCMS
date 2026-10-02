import { describe, expect, it } from 'vitest';
import type { App, Node, Page } from './document';
import { checkVisualSite } from './validate';

const json = (v: unknown) => `${JSON.stringify(v, null, 2)}\n`;
const page = (id: string, children: Node[]): Page => ({
  schemaVersion: 1,
  id,
  title: id,
  root: { id: `${id}-root`, type: 'dcms.page', slots: { default: children } },
});
const app = (extra: Partial<App> = {}): App => ({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }], ...extra });

function site(files: Record<string, unknown>) {
  return Object.fromEntries(Object.entries(files).map(([k, v]) => [k, typeof v === 'string' ? v : json(v)]));
}

const messages = (files: Record<string, unknown>) => checkVisualSite(site(files)).map((p) => `${p.severity}: ${p.message}`);

describe('checkVisualSite', () => {
  it('passes a minimal site', () => {
    expect(messages({ 'dcms/app.json': app(), 'dcms/pages/home.json': page('home', [{ id: 'h', type: 'dcms.heading' }]) })).toEqual([]);
  });

  it('reports a route to a missing page and a site without a home', () => {
    const out = messages({ 'dcms/app.json': app({ routes: [{ id: 'a', path: '/a', page: 'nope' }] }) });
    expect(out).toContain('error: The route /a shows the page “nope”, which does not exist.');
    expect(out).toContain('error: No route serves “/”. The site has no home page.');
  });

  it('applies canPlace, so it refuses what drag and drop refuses', () => {
    const hero: Node = { id: 'x', type: 'dcms.heading', slots: { default: [{ id: 'y', type: 'dcms.text' }] } };
    const nested: Node = { id: 'p', type: 'dcms.section', slots: { default: [{ id: 'q', type: 'dcms.page' }] } };
    const out = messages({ 'dcms/app.json': app(), 'dcms/pages/home.json': page('home', [hero, nested]) });
    expect(out).toContain('warning: Heading has no slot “default”; what is in it is kept but never shown.');
    expect(out).toContain('error: Page cannot be placed inside another component.');
  });

  it('checks prop values and responsive overrides against the component', () => {
    const grid: Node = {
      id: 'g',
      type: 'dcms.grid',
      props: { columns: '9', colour: 'red' },
      responsive: { mobile: { columns: '1' }, tablet: { gap: 'huge' } },
    };
    const text: Node = { id: 't', type: 'dcms.text', responsive: { mobile: { tone: 'muted' } } };
    const out = messages({ 'dcms/app.json': app(), 'dcms/pages/home.json': page('home', [grid, text]) });
    expect(out).toContain('error: Grid › Columns: "9" is not an allowed value.');
    expect(out).toContain('warning: Grid has no setting “colour”; it is ignored.');
    expect(out).toContain('error: Grid › Gap on tablet: "huge" is not an allowed value.');
    expect(out).toContain('warning: Text › “tone” cannot differ on mobile; the override is ignored.');
  });

  it('wants exactly one outlet in the shell and none in pages', () => {
    const shell: Node = { id: 's', type: 'dcms.page', slots: { default: [{ id: 'n', type: 'dcms.nav' }] } };
    const out = messages({
      'dcms/app.json': app({ shell }),
      'dcms/pages/home.json': page('home', [{ id: 'o', type: 'dcms.outlet' }]),
    });
    expect(out).toContain('error: The app shell has no place for the page content.');
    expect(out).toContain('error: Page content can only be placed in the app shell.');
  });

  it('warns about unrouted pages, dead menu links and images without alt text', () => {
    const out = messages({
      'dcms/app.json': app({ navigation: { main: [{ label: 'Gone', to: '/gone' }, { label: 'Ext', to: 'https://x.test' }] } }),
      'dcms/pages/home.json': page('home', [{ id: 'i', type: 'dcms.image', props: { src: '/a.png' } }]),
      'dcms/pages/orphan.json': page('orphan', []),
    });
    expect(out).toEqual([
      'warning: An image has no description (alt text) for people who cannot see it.',
      'warning: No route shows this page, so it is never published.',
      'warning: The “main” menu links to /gone, which no route serves.',
    ]);
  });

  it('reports unreadable documents without throwing', () => {
    const out = messages({ 'dcms/app.json': '{', 'dcms/pages/home.json': '{"schemaVersion": 1}', 'dcms/theme.json': '[' });
    expect(out.filter((m) => m.startsWith('error'))).toHaveLength(3);
  });
});
