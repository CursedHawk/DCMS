import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Breadcrumbs } from './Breadcrumbs';

vi.mock('@tanstack/react-router', () => ({
  Link: ({ to, children }: { to: string; children: React.ReactNode }) => <a href={to}>{children}</a>,
}));

describe('Breadcrumbs', () => {
  it('links every parent, and marks the current page without linking it to itself', () => {
    render(
      <Breadcrumbs
        items={[
          { label: 'Plugins', to: '/plugins' },
          { label: 'CRM', to: '/plugins/crm' },
          { label: 'Records' },
        ]}
      />,
    );

    expect(screen.getAllByRole('link').map((a) => [a.textContent, a.getAttribute('href')])).toEqual([
      ['Plugins', '/plugins'],
      ['CRM', '/plugins/crm'],
    ]);
    const current = screen.getByText('Records');
    expect(current).toHaveAttribute('aria-current', 'page');
    expect(current.closest('a')).toBeNull();
    // The separators are decoration: a list of three places, not five items.
    expect(screen.getAllByRole('listitem')).toHaveLength(3);
  });
});
