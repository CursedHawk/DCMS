// @vitest-environment jsdom
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { describe, expect, it } from 'vitest';
import { DataClientContext, type DataClient } from './data';
import { DataTable } from './dataTable';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const model = {
  tables: [
    {
      apiName: 'deals',
      displayName: 'Deal',
      pluralName: 'Deals',
      primaryField: 'name',
      fields: [
        { apiName: 'name', displayName: 'Name', type: 'text' },
        { apiName: 'amount', displayName: 'Amount', type: 'decimal' },
        { apiName: 'stage', displayName: 'Stage', type: 'choice', options: ['Lead', 'Won'] },
        { apiName: 'company', displayName: 'Company', type: 'lookup', target: 'companies' },
      ],
    },
    { apiName: 'companies', displayName: 'Company', primaryField: 'title', fields: [{ apiName: 'title', displayName: 'Title', type: 'text' }] },
  ],
};

const bigDeals = { id: 'system:1', name: 'Big deals', system: true, mine: false, definition: { columns: ['name', 'amount'], sort: [{ field: 'amount', direction: 'desc' }] } };

function fakeClient(signedIn: boolean) {
  const posts: { path: string; body: Record<string, unknown> }[] = [];
  const sent: { method: string; path: string; body: unknown }[] = [];
  let mine: unknown[] = [];
  const client: DataClient = {
    async get(path) {
      if (path === '/api/crm/_model') return model;
      if (path === '/api/crm/views/deals') return { signedIn, views: [bigDeals, ...mine], defaultView: 'system:1' };
      throw new Error(`404 ${path}`);
    },
    async post(path, body) {
      posts.push({ path, body: body as Record<string, unknown> });
      return { items: [{ id: 'a', name: 'Acme renewal', amount: 1200, company: { id: 'c', title: 'Acme' } }], total: 1 };
    },
    async send(method, path, body) {
      sent.push({ method, path, body });
      if (method === 'POST') {
        const view = { id: 'v1', name: (body as { name: string }).name, system: false, mine: true, definition: (body as { definition: unknown }).definition };
        mine = [view];
        return view;
      }
      return undefined;
    },
  };
  return { client, posts, sent };
}

async function render(data: DataClient, props: Record<string, unknown> = {}): Promise<HTMLElement> {
  const el = document.createElement('div');
  document.body.append(el);
  await act(async () => {
    createRoot(el).render(
      <DataClientContext.Provider value={data}>
        <DataTable nodeId="t" props={{ instance: 'crm', table: 'deals', columns: 'name,amount,company', ...props }} slot={() => null} />
      </DataClientContext.Provider>,
    );
  });
  await settle();
  return el;
}

async function settle() {
  await act(async () => {
    await new Promise((r) => setTimeout(r, 0));
  });
}

function click(el: Element | null | undefined) {
  if (!el) throw new Error('nothing to click');
  act(() => (el as HTMLElement).click());
}

function button(root: HTMLElement, text: string) {
  return Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.trim().startsWith(text));
}

describe('data table', () => {
  it('opens with the default view and asks the server for its sort and columns', async () => {
    const { client, posts } = fakeClient(false);
    const root = await render(client);
    const last = posts.at(-1)!;
    expect(last.path).toBe('/api/crm/data/deals/query');
    expect(last.body.sort).toEqual([{ field: 'amount', direction: 'desc' }]);
    expect(last.body.select).toEqual(['name', 'amount']);
    expect(root.querySelector('tbody')?.textContent).toContain('Acme renewal');
  });

  it('sorts by a header on the server, and shows a lookup by the record it points at', async () => {
    const { client, posts } = fakeClient(false);
    const root = await render(client, { views: false });
    expect(root.querySelector('tbody')?.textContent).toContain('Acme');
    expect(posts.at(-1)!.body.expand).toEqual(['company']);
    expect(root.querySelector('th:nth-child(3) button')).toBeNull();

    click(root.querySelector('th button'));
    await settle();
    expect(posts.at(-1)!.body.sort).toEqual([{ field: 'name', direction: 'asc' }]);
    click(root.querySelector('th button'));
    await settle();
    expect(posts.at(-1)!.body.sort).toEqual([{ field: 'name', direction: 'desc' }]);
    expect(root.querySelector('th')?.getAttribute('aria-sort')).toBe('descending');
  });

  it('offers saving only to a signed-in user, and saves what is shown', async () => {
    const anonymous = await render(fakeClient(false).client);
    expect(button(anonymous, 'Save as')).toBeUndefined();

    const { client, sent } = fakeClient(true);
    const root = await render(client);
    click(button(root, 'Save as'));
    const input = root.querySelector<HTMLInputElement>('.dcms-data-table-panel input')!;
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!;
      setter.call(input, 'My big ones');
      input.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await act(async () => {
      root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    });
    await settle();
    expect(sent[0]).toEqual({
      method: 'POST',
      path: '/api/crm/views/deals',
      body: { name: 'My big ones', definition: { columns: ['name', 'amount'], sort: [{ field: 'amount', direction: 'desc' }] } },
    });
    expect(root.querySelector('select')?.value).toBe('v1');
    expect(button(root, 'Share')).toBeDefined();
  });

  it('compares a text field with the current user, and sends the token for the server to fill', async () => {
    const { client, posts } = fakeClient(true);
    const root = await render(client, { views: false });
    click(button(root, 'Filter'));
    click(button(root, '+ Condition'));
    const source = root.querySelector<HTMLSelectElement>('select[aria-label="Compare with"]')!;
    expect(Array.from(source.options).map((o) => o.textContent)).toEqual(['a value', 'my email', 'my user ID']);
    act(() => {
      source.value = 'email';
      source.dispatchEvent(new Event('change', { bubbles: true }));
    });
    await settle();
    expect(posts.at(-1)!.body.filter).toEqual({ and: [{ field: 'name', op: 'contains', value: { $user: 'email' } }] });
    expect(root.querySelector('input[aria-label="Value"]')).toBeNull();
  });

  it('shows nothing on the site until it is set up', async () => {
    const root = await render(fakeClient(false).client, { instance: '', table: '' });
    expect(root.textContent).toBe('');
  });
});
