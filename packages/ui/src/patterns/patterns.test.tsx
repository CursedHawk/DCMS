import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { user } from '../test/user';
import { FilterBar } from './FilterBar';
import { Meter } from './Meter';
import { StatCard } from './StatCard';

describe('FilterBar', () => {
  it('reports what the reader types', async () => {
    const onSearchChange = vi.fn();
    render(<FilterBar search="" onSearchChange={onSearchChange} searchPlaceholder="Search media" />);
    await user.type(screen.getByRole('searchbox', { name: 'Search media' }), 'a');
    expect(onSearchChange).toHaveBeenCalledWith('a');
  });

  it('omits the search box entirely when the list has no search', () => {
    render(<FilterBar />);
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
  });

  it('says how many filters are narrowing the list', () => {
    // A list that looks empty because of a filter set three minutes ago and forgotten is the
    // most common "the data is gone" report there is.
    render(<FilterBar activeCount={2} onClear={() => {}} />);
    expect(screen.getByRole('button', { name: /Clear filters/ })).toHaveTextContent('2');
  });

  it('hides the clear control when nothing is filtered', () => {
    render(<FilterBar activeCount={0} onClear={() => {}} />);
    expect(screen.queryByRole('button', { name: /Clear filters/ })).not.toBeInTheDocument();
  });

  it('clears', async () => {
    const onClear = vi.fn();
    render(<FilterBar activeCount={1} onClear={onClear} />);
    await user.click(screen.getByRole('button', { name: /Clear filters/ }));
    expect(onClear).toHaveBeenCalledOnce();
  });

  it('renders the filters and the actions the screen supplies', () => {
    render(<FilterBar>{<span>filters</span>}</FilterBar>);
    expect(screen.getByText('filters')).toBeInTheDocument();
  });
});

describe('Meter', () => {
  it('exposes the fill as a percentage to assistive technology', () => {
    render(<Meter fraction={0.42} label="Storage" />);
    expect(screen.getByRole('meter', { name: 'Storage' })).toHaveAttribute('aria-valuenow', '42');
  });

  it('writes the value out — colour alone is not a signal anyone can rely on', () => {
    render(<Meter fraction={0.42} label="Storage" value="3.4 / 8.0 GB" />);
    expect(screen.getByText('3.4 / 8.0 GB')).toBeInTheDocument();
  });

  it('changes colour at the thresholds the platform alerts use', () => {
    const fill = (f: number) =>
      render(<Meter fraction={f} label="x" />).container.querySelector('[role=meter] > div')!.className;
    expect(fill(0.5)).toContain('bg-primary');
    expect(fill(0.85)).toContain('warning');
    expect(fill(0.95)).toContain('bg-destructive');
  });

  it('clamps the bar past full but still reads as critical', () => {
    const { container } = render(<Meter fraction={1.5} label="Over" />);
    const fill = container.querySelector('[role=meter] > div') as HTMLElement;
    expect(fill.style.width).toBe('100%');
    expect(fill.className).toContain('bg-destructive');
  });

  it('survives a fraction that is not a number', () => {
    // A ceiling of zero divides to NaN, and a meter is not the place to find that out.
    const { container } = render(<Meter fraction={Number.NaN} label="x" />);
    expect((container.querySelector('[role=meter] > div') as HTMLElement).style.width).toBe('0%');
  });
});

describe('StatCard', () => {
  it('shows the label and the value', () => {
    render(<StatCard label="Visitors" value="1,204" />);
    expect(screen.getByText('Visitors')).toBeInTheDocument();
    expect(screen.getByText('1,204')).toBeInTheDocument();
  });

  it('shows no trend without a comparison — an arrow with no baseline is not information', () => {
    render(<StatCard label="Visitors" value="1,204" />);
    expect(screen.queryByText(/%/)).not.toBeInTheDocument();
  });

  it('signs the change', () => {
    render(<StatCard label="Visitors" value="1,204" delta={0.12} />);
    expect(screen.getByText('+12%')).toBeInTheDocument();
    render(<StatCard label="Visitors" value="900" delta={-0.08} />);
    expect(screen.getByText('-8%')).toBeInTheDocument();
  });

  it('reads a rise as good or bad depending on what is being counted', () => {
    // More visitors is good; more failed builds is not. The same arrow, the opposite colour.
    const good = render(<StatCard label="Visitors" value="1" delta={0.1} goodDirection="up" />);
    expect(good.container.querySelector('.text-\\[hsl\\(var\\(--success\\)\\)\\]')).not.toBeNull();
    good.unmount();

    const bad = render(<StatCard label="Failed builds" value="1" delta={0.1} goodDirection="down" />);
    expect(bad.container.querySelector('.text-destructive')).not.toBeNull();
  });

  it('treats no change as neutral rather than as a fall', () => {
    render(<StatCard label="Visitors" value="1,204" delta={0} />);
    expect(screen.getByText('0%')).toBeInTheDocument();
  });

  it('hides the value while loading rather than showing a stale one', () => {
    render(<StatCard label="Visitors" value="1,204" isLoading />);
    expect(screen.queryByText('1,204')).not.toBeInTheDocument();
  });

  it('offers an explanation behind an "i" where the number needs one', () => {
    render(<StatCard label="Visitors" value="1" hint="Distinct visitor hashes over the period." />);
    expect(screen.getByRole('button', { name: 'About Visitors' })).toBeInTheDocument();
  });
});
