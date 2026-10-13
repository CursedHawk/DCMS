import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { PermissionProvider } from '@dcms/ui';
import { DashboardPage } from './DashboardPage';

vi.mock('@tanstack/react-router', () => ({
  Link: ({ to, children }: { to: string; children: React.ReactNode }) => <a href={to}>{children}</a>,
}));
vi.mock('../../useAuth', () => ({ useAuth: () => ({ user: { profile: { name: 'Ada' } } }) }));

function shortcuts(permissions: string[], isSuperAdmin = false) {
  render(
    <PermissionProvider value={{ isSuperAdmin, permissions }}>
      <DashboardPage />
    </PermissionProvider>,
  );
  return screen.queryAllByRole('link').map((a) => a.getAttribute('href'));
}

describe('DashboardPage', () => {
  it('offers only the shortcuts the member can follow', () => {
    expect(shortcuts(['content:read'])).toEqual(['/content']);
  });

  it('offers every shortcut to someone who holds every permission', () => {
    expect(shortcuts([], true)).toEqual(['/content', '/media', '/sites', '/plugins', '/settings/domains']);
  });

  it('drops the heading when there is nothing to offer', () => {
    expect(shortcuts([])).toEqual([]);
    expect(screen.queryByRole('heading', { level: 2 })).not.toBeInTheDocument();
  });
});
