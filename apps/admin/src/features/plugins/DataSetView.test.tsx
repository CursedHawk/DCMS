import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import type { DataQuery, DataRow, DataSet } from './dataApi';
import { DataSetView } from './DataSetView';

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}));
vi.mock('../../components/AuthedImage', () => ({ AuthedImage: () => null }));
vi.mock('./configWidgets', () => ({ configWidgets: {} }));
// The form is RJSF's business; what matters here is which values reach it and what is saved.
vi.mock('../../components/SchemaForm', () => ({
  SchemaForm: ({ formData, onChange }: { formData: Record<string, unknown>; onChange: (v: unknown) => void }) => (
    <button type="button" data-testid="form" onClick={() => onChange({ ...formData, displayName: 'Eve Edited' })}>
      {JSON.stringify(formData)}
    </button>
  ),
}));

const set: DataSet = {
  id: 'visitors',
  title: 'Visitors',
  description: null,
  icon: 'Users',
  platform: false,
  canWrite: true,
  columns: [
    { key: 'email', label: 'Email', kind: 'email', sortable: true, primary: true },
    { key: 'tier', label: 'Tier', kind: 'badge', sortable: false, primary: false },
    { key: 'createdAt', label: 'Registered', kind: 'datetime', sortable: true, primary: false },
  ],
  itemSchema: { type: 'object', properties: { displayName: { type: 'string' } } },
  filters: [],
  actions: [{ id: 'verify', label: 'Mark email verified', risk: 'safe', bulk: true, description: null, inputSchema: null }],
  searchable: true,
  canCreate: false,
  canUpdate: true,
  canDelete: true,
  canDownload: false,
  defaultSort: 'createdAt',
  defaultDescending: true,
};

const rows: DataRow[] = [
  { key: 'a', title: null, values: { email: 'eve@site.test', tier: 'gold', createdAt: '2026-09-01T10:00:00Z' } },
  { key: 'b', title: null, values: { email: 'max@site.test', tier: null, createdAt: '2026-09-02T10:00:00Z' } },
];

const eve = { key: 'a', title: 'Eve', values: { email: 'eve@site.test', displayName: 'Eve', createdAt: '2026-09-01T10:00:00Z' } };
const pageQuery = vi.hoisted(() => vi.fn());
const api = vi.hoisted(() => ({
  update: vi.fn(() => Promise.resolve({})),
  action: vi.fn(() => Promise.resolve({ affected: 2, message: null })),
  remove: vi.fn(() => Promise.resolve()),
  create: vi.fn(),
  download: vi.fn(),
}));
vi.mock('./dataApi', () => ({
  useDataPage: (_slug: string, _set: string, query: DataQuery) => {
    pageQuery(query);
    return { data: { rows, total: 2, page: 1, pageSize: 25 }, isLoading: false, isError: false, isFetching: false };
  },
  // Stable per key, as react-query's data is.
  useDataRow: (_slug: string, _set: string, key: string | null) => ({ data: key ? eve : undefined, isLoading: false }),
  dataApi: api,
}));

function renderView() {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <DataSetView slug="members" set={set} />
    </QueryClientProvider>,
  );
}

describe('DataSetView', () => {
  it('renders the columns the plugin describes and asks the server for its default sort', () => {
    renderView();
    const table = screen.getByRole('table');
    expect(within(table).getByText('eve@site.test')).toBeTruthy();
    expect(within(table).getByText('gold')).toBeTruthy();
    expect(pageQuery).toHaveBeenLastCalledWith(expect.objectContaining({ sort: 'createdAt', descending: true, page: 1 }));
  });

  it('sends the search to the server', async () => {
    renderView();
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'eve' } });
    await waitFor(() => expect(pageQuery).toHaveBeenLastCalledWith(expect.objectContaining({ search: 'eve' })));
  });

  it('runs a bulk action on the selected rows', async () => {
    renderView();
    const table = screen.getByRole('table');
    fireEvent.click(within(table).getByRole('checkbox', { name: 'Select all' }));
    fireEvent.click(screen.getByRole('button', { name: 'Mark email verified (2)' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Mark email verified' }));
    await waitFor(() => expect(api.action).toHaveBeenCalledWith('members', 'visitors', 'verify', ['a', 'b'], undefined));
  });

  it('edits only the item schema values and shows the rest as details', async () => {
    renderView();
    fireEvent.click(within(screen.getByRole('table')).getByText('eve@site.test'));
    const sheet = await screen.findByRole('dialog');
    expect(within(sheet).getByTestId('form').textContent).toBe('{"displayName":"Eve"}');
    expect(within(sheet).getByText('Email')).toBeTruthy();
    fireEvent.click(within(sheet).getByTestId('form'));
    fireEvent.click(within(sheet).getByRole('button', { name: 'actions.save' }));
    await waitFor(() => expect(api.update).toHaveBeenCalledWith('members', 'visitors', 'a', { displayName: 'Eve Edited' }));
  });
});
