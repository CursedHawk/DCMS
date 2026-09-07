import { describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { user } from '../test/user';
import { DataTable, type Column } from './DataTable';

interface Row {
  id: string;
  name: string;
  size: number | null;
  status: string;
}

const rows: Row[] = [
  { id: '1', name: 'banner.png', size: 2048, status: 'Ready' },
  { id: '2', name: 'Avatar.jpg', size: null, status: 'Processing' },
  { id: '3', name: 'clip.mp4', size: 512, status: 'Failed' },
];

const columns: Column<Row>[] = [
  { id: 'name', header: 'Name', cell: (r) => r.name, sortValue: (r) => r.name, primary: true },
  { id: 'size', header: 'Size', cell: (r) => r.size ?? '—', sortValue: (r) => r.size, align: 'right' },
  { id: 'status', header: 'Status', cell: (r) => r.status },
];

function Table(props: Partial<React.ComponentProps<typeof DataTable<Row>>> = {}) {
  return <DataTable rows={rows} columns={columns} rowKey={(r) => r.id} {...props} />;
}

/** The <table>, ignoring the card list that renders alongside it for narrow screens. */
const grid = () => screen.getByRole('table');
const bodyRows = () => within(grid()).getAllByRole('row').slice(1);
const names = () => bodyRows().map((r) => within(r).getAllByRole('cell')[0].textContent);

describe('DataTable', () => {
  it('renders a row per record', () => {
    render(<Table />);
    expect(bodyRows()).toHaveLength(3);
  });

  it('renders both a table and a card list, and lets CSS choose', () => {
    // Below `md` a seven-column table is either a horizontal scroll nobody finds or a squeeze
    // that makes every column unreadable. Both layouts are in the DOM; the breakpoint decides.
    render(<Table />);
    expect(screen.getByRole('table')).toBeInTheDocument();
    expect(screen.getByRole('list')).toBeInTheDocument();
    expect(within(screen.getByRole('list')).getAllByRole('listitem')).toHaveLength(3);
  });

  describe('sorting', () => {
    it('does not sort until asked', () => {
      render(<Table />);
      expect(names()).toEqual(['banner.png', 'Avatar.jpg', 'clip.mp4']);
    });

    it('sorts ascending on the first click', async () => {
      render(<Table />);
      await user.click(screen.getByRole('button', { name: 'Sort by Name' }));
      expect(names()).toEqual(['Avatar.jpg', 'banner.png', 'clip.mp4']);
    });

    it('sorts case-insensitively — localeCompare, not codepoint order', async () => {
      // A plain `<` puts every capitalised name before every lowercase one, which reads as
      // random to anyone who did not write it.
      render(<Table />);
      await user.click(screen.getByRole('button', { name: 'Sort by Name' }));
      expect(names()?.[0]).toBe('Avatar.jpg');
    });

    it('reverses on the second click and clears on the third', async () => {
      render(<Table />);
      const header = screen.getByRole('button', { name: 'Sort by Name' });
      await user.click(header);
      await user.click(header);
      expect(names()).toEqual(['clip.mp4', 'banner.png', 'Avatar.jpg']);
      await user.click(header);
      expect(names()).toEqual(['banner.png', 'Avatar.jpg', 'clip.mp4']);
    });

    it('sinks absent values in BOTH directions', async () => {
      // An empty cell is not "smallest", it is missing. Sorting it to the top when descending
      // buries the largest value under a row that has none.
      render(<Table />);
      const header = screen.getByRole('button', { name: 'Sort by Size' });
      await user.click(header);
      expect(names()?.at(-1)).toBe('Avatar.jpg');
      await user.click(header);
      expect(names()?.at(-1)).toBe('Avatar.jpg');
    });

    it('announces the sort to assistive technology', async () => {
      render(<Table />);
      await user.click(screen.getByRole('button', { name: 'Sort by Name' }));
      expect(within(grid()).getByRole('columnheader', { name: /Name/ })).toHaveAttribute(
        'aria-sort',
        'ascending',
      );
    });

    it('offers no sort control for a column that cannot be sorted', () => {
      render(<Table />);
      expect(screen.queryByRole('button', { name: 'Sort by Status' })).not.toBeInTheDocument();
    });

    it('honours a default sort', () => {
      render(<Table defaultSort={{ columnId: 'name', direction: 'desc' }} />);
      expect(names()).toEqual(['clip.mp4', 'banner.png', 'Avatar.jpg']);
    });
  });

  describe('states', () => {
    it('shows the empty slot only for a genuinely empty list', () => {
      render(<Table rows={[]} empty={<p>No media yet</p>} />);
      expect(screen.getByText('No media yet')).toBeInTheDocument();
      expect(screen.queryByRole('table')).not.toBeInTheDocument();
    });

    it('does not claim emptiness while loading', () => {
      // "No media yet" during a request is a lie the reader acts on.
      render(<Table rows={undefined} isLoading empty={<p>No media yet</p>} />);
      expect(screen.queryByText('No media yet')).not.toBeInTheDocument();
      expect(screen.getByText('Loading…')).toBeInTheDocument();
    });

    it('shows an error instead of an empty list — a failure is not an absence', () => {
      render(<Table rows={[]} error={<p>Could not load</p>} empty={<p>No media yet</p>} />);
      expect(screen.getByText('Could not load')).toBeInTheDocument();
      expect(screen.queryByText('No media yet')).not.toBeInTheDocument();
    });

    it('marks the loading region busy', () => {
      render(<Table rows={undefined} isLoading />);
      expect(screen.getByRole('status', { busy: true })).toBeInTheDocument();
    });
  });

  describe('selection', () => {
    const selectionProps = (selected: string[] = []) => {
      const onChange = vi.fn();
      return {
        onChange,
        selection: { selected: new Set(selected), onChange },
      };
    };

    it('selects a row', async () => {
      const { selection, onChange } = selectionProps();
      render(<Table selection={selection} />);
      await user.click(within(grid()).getAllByRole('checkbox', { name: 'Select row' })[0]);
      expect(onChange).toHaveBeenCalledWith(new Set(['1']));
    });

    it('deselects a selected row', async () => {
      const { selection, onChange } = selectionProps(['1']);
      render(<Table selection={selection} />);
      await user.click(within(grid()).getAllByRole('checkbox', { name: 'Select row' })[0]);
      expect(onChange).toHaveBeenCalledWith(new Set());
    });

    it('selects everything from the header', async () => {
      const { selection, onChange } = selectionProps();
      render(<Table selection={selection} />);
      await user.click(within(grid()).getByRole('checkbox', { name: 'Select all' }));
      expect(onChange).toHaveBeenCalledWith(new Set(['1', '2', '3']));
    });

    it('clears everything when all are already selected', async () => {
      const { selection, onChange } = selectionProps(['1', '2', '3']);
      render(<Table selection={selection} />);
      await user.click(within(grid()).getByRole('checkbox', { name: 'Select all' }));
      expect(onChange).toHaveBeenCalledWith(new Set());
    });

    it('shows a partial selection as indeterminate, not as ticked', async () => {
      // A ticked select-all over a partial selection says "everything", and the next click
      // then deselects rather than completing — the opposite of what the reader expects.
      const { selection } = selectionProps(['1']);
      render(<Table selection={selection} />);
      expect(within(grid()).getByRole('checkbox', { name: 'Select all' })).toHaveAttribute(
        'data-state',
        'indeterminate',
      );
    });

    it('does not open a row when the checkbox is clicked', async () => {
      const onRowClick = vi.fn();
      const { selection } = selectionProps();
      render(<Table selection={selection} onRowClick={onRowClick} />);
      await user.click(within(grid()).getAllByRole('checkbox', { name: 'Select row' })[0]);
      expect(onRowClick).not.toHaveBeenCalled();
    });
  });

  it('opens a row when the row itself is clicked', async () => {
    const onRowClick = vi.fn();
    render(<Table onRowClick={onRowClick} />);
    await user.click(within(bodyRows()[0]).getAllByRole('cell')[0]);
    expect(onRowClick).toHaveBeenCalledWith(rows[0]);
  });

  it('describes the table for screen readers when asked', () => {
    render(<Table caption="Media assets" />);
    expect(screen.getByRole('table', { name: 'Media assets' })).toBeInTheDocument();
  });
});
