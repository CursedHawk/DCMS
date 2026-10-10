import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { HostLink } from './HostLink';

describe('HostLink', () => {
  it('opens a bare hostname over https, in a new tab that cannot reach back', () => {
    render(<HostLink host="acme.dcms.highgeek.eu" />);
    const link = screen.getByRole('link', { name: 'acme.dcms.highgeek.eu' });
    expect(link).toHaveAttribute('href', 'https://acme.dcms.highgeek.eu');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  });

  it('shows a wildcard as text, since there is no one site to open', () => {
    render(<HostLink host="*.highgeek.eu" />);
    expect(screen.getByText('*.highgeek.eu')).toBeInTheDocument();
    expect(screen.queryByRole('link')).toBeNull();
  });

  it('keeps a URL it is given as it is', () => {
    render(<HostLink host="http://localhost:8080" />);
    expect(screen.getByRole('link')).toHaveAttribute('href', 'http://localhost:8080');
  });
});
