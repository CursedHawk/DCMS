// @vitest-environment jsdom
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { loadDocuments, registryFor } from './app';
import { RenderNode } from './render';
import { checkVisualSite } from './validate';

const json = (v: unknown) => `${JSON.stringify(v, null, 2)}\n`;
const contract = { schemaVersion: 1, name: 'countdown', label: 'Countdown', props: [{ kind: 'text', name: 'until', label: 'Until', default: 'soon' }] };
const node = { id: 'c', type: 'code.countdown', props: { until: 'Friday' } };

describe('developer components', () => {
  const modules = { '../dcms/code/countdown.json': contract, '../dcms/app.json': { schemaVersion: 1, routes: [] } };

  it('render the developer’s component where code may run, with the props as React props', () => {
    const Countdown = ({ until }: { until: string }) => <time>until {until}</time>;
    const docs = loadDocuments(modules, { './components/countdown.tsx': Countdown });
    expect(renderToStaticMarkup(<RenderNode node={node} registry={registryFor(docs)} />)).toContain('<time>until Friday</time>');
  });

  it('are a placeholder where it may not — the canvas loads no developer code at all', () => {
    const html = renderToStaticMarkup(<RenderNode node={node} registry={registryFor(loadDocuments(modules))} />);
    expect(html).toContain('dcms-code-placeholder');
    expect(html).not.toContain('<time>');
  });

  it('are checked: a contract needs a source with a default export, a source needs a contract', () => {
    const site = (extra: Record<string, string>) => ({
      'dcms/app.json': json({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }),
      'dcms/pages/home.json': json({ schemaVersion: 1, id: 'home', title: 'Home', root: { id: 'r', type: 'dcms.page', slots: { default: [node] } } }),
      'dcms/code/countdown.json': json(contract),
      ...extra,
    });
    const messages = (files: Record<string, string>) => checkVisualSite(files).map((p) => `${p.severity} ${p.file}: ${p.message}`);
    expect(messages(site({}))).toEqual(['error dcms/code/countdown.json: has no source: create src/components/countdown.tsx.']);
    expect(messages(site({ 'src/components/countdown.tsx': 'export function Countdown() {}' }))).toEqual([
      'error src/components/countdown.tsx: needs a default export: the component the builder places.',
    ]);
    expect(messages(site({ 'src/components/countdown.tsx': 'export default function Countdown({ until }) {}', 'src/components/stray.tsx': 'export default 1' }))).toEqual([
      'warning src/components/stray.tsx: has no contract, so the builder cannot place it: add dcms/code/stray.json.',
    ]);
    // A setting the source never mentions cannot be doing anything.
    expect(messages(site({ 'src/components/countdown.tsx': 'export default function Countdown({ untilDate }) {}' }))).toEqual([
      'warning src/components/countdown.tsx: never reads “until”, which dcms/code/countdown.json offers authors as a setting.',
    ]);
  });
});
