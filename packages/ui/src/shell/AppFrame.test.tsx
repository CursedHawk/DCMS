import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppFrame, useSidebarCollapse } from './AppFrame';

/** Drives the `(min-width: 1024px)` query the frame listens to. */
function stubViewport(wide: boolean) {
  const listeners = new Set<() => void>();
  window.matchMedia = ((query: string) => ({
    // A getter, not a snapshot: the frame holds the MediaQueryList it got and reads `.matches`
    // when the change event fires, so a captured boolean would report the old viewport.
    get matches() {
      return wide;
    },
    media: query,
    onchange: null,
    addEventListener: (_: string, fn: () => void) => void listeners.add(fn),
    removeEventListener: (_: string, fn: () => void) => void listeners.delete(fn),
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  return {
    widen() {
      wide = true;
      listeners.forEach((fn) => fn());
    },
    listenerCount: () => listeners.size,
  };
}

function Frame(props: Partial<React.ComponentProps<typeof AppFrame>> = {}) {
  return (
    <AppFrame
      sidebar={({ inDrawer }) => <nav>{inDrawer ? 'drawer nav' : 'rail nav'}</nav>}
      topbar={({ menuButton }) => <header>{menuButton}</header>}
      {...props}
    >
      <p>page content</p>
    </AppFrame>
  );
}

beforeEach(() => {
  localStorage.clear();
  stubViewport(true);
});

describe('AppFrame', () => {
  it('renders the rail, the top bar and the page', () => {
    render(<Frame />);
    expect(screen.getByText('rail nav')).toBeInTheDocument();
    expect(screen.getByText('page content')).toBeInTheDocument();
  });

  it('keeps the drawer closed until asked', () => {
    render(<Frame />);
    expect(screen.queryByText('drawer nav')).not.toBeInTheDocument();
  });

  it('opens the drawer from the menu button, with the drawer copy of the nav', async () => {
    render(<Frame />);
    await userEvent.click(screen.getByRole('button', { name: 'Open navigation' }));
    expect(await screen.findByText('drawer nav')).toBeInTheDocument();
  });

  it('closes the drawer on Escape', async () => {
    render(<Frame />);
    await userEvent.click(screen.getByRole('button', { name: 'Open navigation' }));
    await screen.findByText('drawer nav');
    await userEvent.keyboard('{Escape}');
    expect(screen.queryByText('drawer nav')).not.toBeInTheDocument();
  });

  it('closes the drawer when the viewport grows past the breakpoint', async () => {
    // Otherwise resizing with the drawer open strands an invisible modal holding the focus
    // trap and the scroll lock, and the page appears frozen.
    const mq = stubViewport(false);
    render(<Frame />);
    await userEvent.click(screen.getByRole('button', { name: 'Open navigation' }));
    await screen.findByText('drawer nav');
    mq.widen();
    await vi.waitFor(() => expect(screen.queryByText('drawer nav')).not.toBeInTheDocument());
  });

  it('names the drawer for screen readers', async () => {
    render(<Frame navLabel="Platform sections" />);
    await userEvent.click(screen.getByRole('button', { name: 'Open navigation' }));
    expect(await screen.findByRole('dialog', { name: 'Platform sections' })).toBeInTheDocument();
  });

  it('renders a banner above the top bar when given one', () => {
    render(<Frame banner={<div>You are on production</div>} />);
    expect(screen.getByText('You are on production')).toBeInTheDocument();
  });

  it('lets the app own the drawer state', async () => {
    const onOpenChange = vi.fn();
    render(<Frame drawerOpen={false} onDrawerOpenChange={onOpenChange} />);
    await userEvent.click(screen.getByRole('button', { name: 'Open navigation' }));
    expect(onOpenChange).toHaveBeenCalledWith(true);
    // Controlled: it stays shut until the app says otherwise.
    expect(screen.queryByText('drawer nav')).not.toBeInTheDocument();
  });
});

function Collapse() {
  const [collapsed, toggle] = useSidebarCollapse();
  return (
    <button onClick={toggle}>{collapsed ? 'collapsed' : 'expanded'}</button>
  );
}

describe('useSidebarCollapse', () => {
  it('starts expanded and remembers a collapse', async () => {
    render(<Collapse />);
    expect(screen.getByRole('button')).toHaveTextContent('expanded');
    await userEvent.click(screen.getByRole('button'));
    expect(screen.getByRole('button')).toHaveTextContent('collapsed');
    expect(localStorage.getItem('dcms.sidebar.collapsed')).toBe('1');
  });

  it('restores the remembered state', () => {
    localStorage.setItem('dcms.sidebar.collapsed', '1');
    render(<Collapse />);
    expect(screen.getByRole('button')).toHaveTextContent('collapsed');
  });

  it('still renders when storage throws, which is a real browser setting', () => {
    const spy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('blocked');
    });
    expect(() => render(<Collapse />)).not.toThrow();
    expect(screen.getByRole('button')).toHaveTextContent('expanded');
    spy.mockRestore();
  });
});
