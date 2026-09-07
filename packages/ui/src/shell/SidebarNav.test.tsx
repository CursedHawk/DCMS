import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Home, Image, Settings, Users } from 'lucide-react';
import { TooltipProvider } from '../ui/tooltip';
import { NavLinkBody, SidebarNav } from './SidebarNav';
import type { ShellNavItem } from './nav';

const items: ShellNavItem[] = [
  { to: '/', label: 'Dashboard', icon: Home },
  { to: '/media', label: 'Media', icon: Image, group: 'build', badge: 3 },
  { to: '/members', label: 'Members', icon: Users, group: 'admin' },
  { to: '/settings', label: 'Settings', icon: Settings, group: 'admin' },
];
const groups = [
  { id: 'build', label: 'Build' },
  { id: 'admin', label: 'Administer' },
];

function Nav(props: Partial<React.ComponentProps<typeof SidebarNav>> = {}) {
  return (
    <TooltipProvider>
      <SidebarNav
        items={items}
        groups={groups}
        pathname="/media"
        renderLink={(item, { active, collapsed, className, onNavigate }) => (
          <a
            key={item.to}
            href={item.to}
            className={className}
            aria-current={active ? 'page' : undefined}
            onClick={(e) => {
              e.preventDefault();
              onNavigate?.();
            }}
          >
            <NavLinkBody item={item} collapsed={collapsed} />
          </a>
        )}
        {...props}
      />
    </TooltipProvider>
  );
}

describe('SidebarNav', () => {
  it('renders every item the app handed it', () => {
    render(<Nav />);
    for (const label of ['Dashboard', 'Media', 'Members', 'Settings']) {
      expect(screen.getByRole('link', { name: new RegExp(label) })).toBeInTheDocument();
    }
  });

  it('marks exactly one link as the current page', () => {
    render(<Nav />);
    const current = screen.getAllByRole('link').filter((l) => l.getAttribute('aria-current') === 'page');
    expect(current).toHaveLength(1);
    expect(current[0]).toHaveAccessibleName(/Media/);
  });

  it('groups items under their headings, and skips a group with nothing in it', () => {
    render(<Nav groups={[...groups, { id: 'empty', label: 'Nothing here' }]} />);
    expect(screen.getByRole('heading', { name: 'Build' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Nothing here' })).not.toBeInTheDocument();
  });

  it('renders ungrouped items even when no groups are given at all', () => {
    render(<Nav items={[items[0]]} groups={[]} />);
    expect(screen.getByRole('link', { name: /Dashboard/ })).toBeInTheDocument();
  });

  it('shows a badge count, and caps it so it cannot widen the rail', () => {
    render(<Nav items={[{ ...items[1], badge: 1200 }]} />);
    expect(screen.getByText('99+')).toBeInTheDocument();
  });

  it('has a labelled landmark, so a screen reader can jump to it', () => {
    render(<Nav ariaLabel="Platform sections" />);
    expect(screen.getByRole('navigation', { name: 'Platform sections' })).toBeInTheDocument();
  });

  it('calls onNavigate when a destination is chosen — this is what closes the drawer', async () => {
    const onNavigate = vi.fn();
    render(<Nav onNavigate={onNavigate} />);
    await userEvent.click(screen.getByRole('link', { name: /Members/ }));
    expect(onNavigate).toHaveBeenCalledOnce();
  });

  describe('collapsed', () => {
    it('keeps every label reachable to a screen reader', () => {
      render(<Nav collapsed onToggleCollapse={() => {}} />);
      // Visually icons only, but the accessible name must survive or the rail is a column
      // of anonymous glyphs.
      expect(screen.getByRole('link', { name: 'Members' })).toBeInTheDocument();
    });

    it('hides the group headings, which have no icon to stand in for them', () => {
      render(<Nav collapsed onToggleCollapse={() => {}} />);
      expect(screen.queryByRole('heading', { name: 'Build' })).not.toBeInTheDocument();
    });
  });

  describe('the collapse toggle', () => {
    it('is absent when the app does not offer collapsing', () => {
      render(<Nav />);
      expect(screen.queryByRole('button', { name: 'Collapse' })).not.toBeInTheDocument();
    });

    it('reports its state, so it is not just an unlabelled arrow', async () => {
      const onToggle = vi.fn();
      render(<Nav onToggleCollapse={onToggle} collapsed={false} />);
      const button = screen.getByRole('button', { name: 'Collapse' });
      expect(button).toHaveAttribute('aria-pressed', 'false');
      await userEvent.click(button);
      expect(onToggle).toHaveBeenCalledOnce();
    });
  });
});
