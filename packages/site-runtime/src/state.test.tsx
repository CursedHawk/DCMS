// @vitest-environment jsdom
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { RouterProvider, createMemoryRouter } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import { loadDocuments, siteRoutes } from './app';
import { resetPageState } from './state';
import { checkVisualSite } from './validate';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
afterEach(resetPageState);

const json = (v: unknown) => `${JSON.stringify(v, null, 2)}\n`;

// Two tabs: buttons set `tab`, each panel shows while `tab` is its own; a toggle opens details.
const page = {
  schemaVersion: 1,
  id: 'home',
  title: 'Home',
  state: { tab: 'dates', details: false },
  root: {
    id: 'r',
    type: 'dcms.page',
    slots: {
      default: [
        { id: 'b1', type: 'dcms.button', props: { label: 'Dates' }, action: { type: 'set-state', key: 'tab', value: 'dates' } },
        { id: 'b2', type: 'dcms.button', props: { label: 'Venues' }, action: { type: 'set-state', key: 'tab', value: 'venues' } },
        { id: 'p1', type: 'dcms.text', props: { text: 'Dates panel' }, when: { state: 'tab', equals: 'dates' } },
        { id: 'p2', type: 'dcms.text', props: { text: 'Venues panel' }, when: { state: 'tab', equals: 'venues' } },
        { id: 'b3', type: 'dcms.button', props: { label: 'More' }, action: { type: 'toggle-state', key: 'details' } },
        { id: 'd', type: 'dcms.text', props: { text: 'Details' }, when: { state: 'details' } },
      ],
    },
  },
};

describe('page state', () => {
  it('drives tabs and toggles on the site', async () => {
    const documents = loadDocuments({ 'dcms/app.json': { schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }, 'dcms/pages/home.json': page });
    const el = document.createElement('div');
    await act(async () => createRoot(el).render(<RouterProvider router={createMemoryRouter(siteRoutes(documents))} />));
    const click = (label: string) => act(async () => [...el.querySelectorAll('button')].find((b) => b.textContent === label)!.click());
    const text = () => [...el.querySelectorAll('p')].map((p) => p.textContent);

    expect(text()).toEqual(['Dates panel']);
    await click('Venues');
    expect(text()).toEqual(['Venues panel']);
    await click('More');
    expect(text()).toEqual(['Venues panel', 'Details']);
    await click('More');
    expect(text()).toEqual(['Venues panel']);
  });

  it('is checked: undeclared names, toggling a non-boolean, and state in the shell', () => {
    const bad = structuredClone(page);
    bad.root.slots.default[0]!.action = { type: 'set-state', key: 'nope', value: 'x' };
    bad.root.slots.default[4]!.action = { type: 'toggle-state', key: 'tab' };
    const problems = checkVisualSite({
      'dcms/app.json': json({
        schemaVersion: 1,
        routes: [{ id: 'home', path: '/', page: 'home' }],
        shell: { id: 's', type: 'dcms.page', slots: { default: [{ id: 'o', type: 'dcms.outlet' }, { id: 'x', type: 'dcms.text', when: { state: 'tab' } }] } },
      }),
      'dcms/pages/home.json': json(bad),
    }).map((p) => p.message);
    expect(problems).toEqual(
      expect.arrayContaining([
        'This page declares no state “nope”.',
        '“tab” is not true/false, so it cannot be toggled.',
        'The app shell has no page state, so “tab” means nothing here.',
      ]),
    );
  });
});
