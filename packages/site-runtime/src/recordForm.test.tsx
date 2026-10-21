// @vitest-environment jsdom
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { builtinRegistry } from './components';
import { DataClientContext, ItemContext, type DataClient } from './data';
import type { Node } from './document';
import { RenderNode } from './render';
import { RenderModeContext } from './renderMode';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const MODEL = {
  tables: [
    {
      apiName: 'deals',
      displayName: 'Deal',
      fields: [
        { apiName: 'name', displayName: 'Name', type: 'text', required: true },
        { apiName: 'amount', displayName: 'Amount', type: 'decimal' },
        { apiName: 'stage', displayName: 'Stage', type: 'choice', options: ['lead', 'won'] },
        { apiName: 'won', displayName: 'Won', type: 'boolean' },
        { apiName: 'ref', displayName: 'Ref', type: 'text', readOnly: true },
      ],
    },
  ],
};

function client(): DataClient & { writes: { method: string; path: string; body: unknown }[] } {
  const writes: { method: string; path: string; body: unknown }[] = [];
  return {
    writes,
    async get(path) {
      if (path === '/api/crm/_model') return MODEL;
      throw new Error(`404 ${path}`);
    },
    async post(path, body) {
      writes.push({ method: 'POST', path, body });
      return { id: 'new-id' };
    },
    async send(method, path, body) {
      writes.push({ method, path, body });
      return { id: 'r1' };
    },
  };
}

async function render(ui: React.ReactNode, data: DataClient, mode: 'live' | 'edit' = 'live'): Promise<HTMLElement> {
  const el = document.createElement('div');
  document.body.append(el);
  await act(async () => {
    createRoot(el).render(
      <RenderModeContext.Provider value={mode}>
        <DataClientContext.Provider value={data}>{ui}</DataClientContext.Provider>
      </RenderModeContext.Provider>,
    );
  });
  await act(async () => {
    await new Promise((r) => setTimeout(r, 0));
  });
  return el;
}

async function submit(el: HTMLElement) {
  await act(async () => {
    el.querySelector('form')!.requestSubmit();
    await new Promise((r) => setTimeout(r, 0));
  });
}

const form = (props: Record<string, unknown>): Node => ({ id: 'f', type: 'dcms.record-form', props: { instance: 'crm', table: 'deals', ...props } });

describe('Record form', () => {
  it('draws the table’s writable fields and creates a record with typed values', async () => {
    const data = client();
    const el = await render(<RenderNode node={form({})} registry={builtinRegistry} />, data);
    // The read-only field is not offered.
    expect([...el.querySelectorAll('label')].map((l) => l.textContent?.trim())).toEqual(['Name *', 'Amount', 'Stage', 'Won']);
    (el.querySelector('input[name="name"]') as HTMLInputElement).value = 'Big one';
    (el.querySelector('input[name="amount"]') as HTMLInputElement).value = '12.5';
    (el.querySelector('select[name="stage"]') as HTMLSelectElement).value = 'won';
    await submit(el);
    expect(data.writes).toEqual([{ method: 'POST', path: '/api/crm/data/deals', body: { name: 'Big one', amount: 12.5, stage: 'won', won: false } }]);
    expect(el.textContent).toContain('Saved.');
  });

  it('on a detail page, edits the record it shows, with the version it read', async () => {
    const data = client();
    const scope = { item: { id: 'r1', slug: 'r1', data: { id: 'r1', version: 4, name: 'Old', amount: 3, stage: 'lead', won: true } }, index: 0, count: 1 };
    const el = await render(
      <ItemContext.Provider value={scope}>
        <RenderNode node={form({ mode: 'edit', fields: 'name, amount' })} registry={builtinRegistry} />
      </ItemContext.Provider>,
      data,
    );
    expect((el.querySelector('input[name="name"]') as HTMLInputElement).value).toBe('Old');
    (el.querySelector('input[name="amount"]') as HTMLInputElement).value = '';
    await submit(el);
    expect(data.writes).toEqual([{ method: 'PATCH', path: '/api/crm/data/deals/r1', body: { name: 'Old', amount: null, version: 4 } }]);
  });

  it('never saves from the canvas, and names a table the site cannot write to', async () => {
    const data = client();
    const el = await render(<RenderNode node={form({})} registry={builtinRegistry} />, data, 'edit');
    await submit(el);
    expect(data.writes).toEqual([]);
    const missing = await render(<RenderNode node={form({ table: 'leads' })} registry={builtinRegistry} />, data, 'edit');
    expect(missing.textContent).toContain('“leads” is not a table the site can write to');
  });
});

describe('Record form dates', () => {
  afterEach(() => vi.unstubAllEnvs());

  it('shows a stored time in the visitor’s zone, so saving it unchanged keeps it', async () => {
    vi.stubEnv('TZ', 'Europe/Prague');
    const data = client();
    const model = { tables: [{ apiName: 'deals', displayName: 'Deal', fields: [{ apiName: 'closeAt', displayName: 'Close', type: 'dateTime' }] }] };
    data.get = async (path) => {
      if (path === '/api/crm/_model') return model;
      throw new Error(`404 ${path}`);
    };
    const scope = { item: { id: 'r1', slug: 'r1', data: { id: 'r1', version: 1, closeAt: '2026-10-07T16:00:00Z' } }, index: 0, count: 1 };
    const el = await render(
      <ItemContext.Provider value={scope}>
        <RenderNode node={form({ mode: 'edit' })} registry={builtinRegistry} />
      </ItemContext.Provider>,
      data,
    );

    expect((el.querySelector('input[name="closeAt"]') as HTMLInputElement).value).toBe('2026-10-07T18:00');
    await submit(el);
    expect((data.writes[0]!.body as Record<string, unknown>).closeAt).toBe('2026-10-07T16:00:00.000Z');
  });
});
