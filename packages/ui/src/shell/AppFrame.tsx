import { Menu } from 'lucide-react';
import { useEffect, useState } from 'react';
import { cn } from '../cn';
import { Button } from '../ui/button';
import { Sheet, SheetContent, SheetTitle } from '../ui/sheet';

const COLLAPSE_KEY = 'dcms.sidebar.collapsed';

/**
 * Sidebar collapse, remembered.
 *
 * Reads and writes are wrapped because `localStorage` throws outright in a browser configured
 * to block site data, and a shell that cannot render because it could not read a preference
 * is a worse outcome than a shell that forgets one.
 */
export function useSidebarCollapse(): [boolean, () => void] {
  const [collapsed, setCollapsed] = useState(() => {
    try {
      return localStorage.getItem(COLLAPSE_KEY) === '1';
    } catch {
      return false;
    }
  });

  const toggle = () =>
    setCollapsed((was) => {
      try {
        localStorage.setItem(COLLAPSE_KEY, was ? '0' : '1');
      } catch {
        /* preference not persisted; the session still works */
      }
      return !was;
    });

  return [collapsed, toggle];
}

/**
 * The application frame both consoles sit in.
 *
 * <p>One layout, two behaviours by viewport. At `lg` and above the sidebar is a persistent
 * column. Below it the column would eat most of a phone screen, so it becomes a drawer behind
 * a button in the top bar — which is the whole reason the admin SPA was unusable on a phone:
 * it rendered a fixed 248px `<aside>` at every width, with no breakpoint and no way to
 * dismiss it.</p>
 *
 * <p>`h-dvh` rather than `h-screen`: on mobile Safari `100vh` is the viewport *without* the
 * browser chrome, so a full-height shell is taller than the space it has and the bottom of
 * every page hides behind the toolbar.</p>
 */
export function AppFrame({
  sidebar,
  topbar,
  banner,
  children,
  navLabel = 'Navigation',
  menuLabel = 'Open navigation',
  drawerOpen,
  onDrawerOpenChange,
}: {
  /** Rendered twice — as the persistent column and inside the drawer. */
  sidebar: (context: { inDrawer: boolean }) => React.ReactNode;
  topbar: (context: { menuButton: React.ReactNode }) => React.ReactNode;
  /** Full-width strip above the top bar. The platform console's environment band. */
  banner?: React.ReactNode;
  children: React.ReactNode;
  navLabel?: string;
  menuLabel?: string;
  drawerOpen?: boolean;
  onDrawerOpenChange?: (open: boolean) => void;
}) {
  const [uncontrolled, setUncontrolled] = useState(false);
  const open = drawerOpen ?? uncontrolled;
  const setOpen = onDrawerOpenChange ?? setUncontrolled;

  /*
   * Close the drawer when the viewport grows past the breakpoint. Without this, resizing a
   * window with the drawer open leaves an invisible open Dialog holding the focus trap and
   * the scroll lock, and the page appears frozen.
   */
  useEffect(() => {
    if (!open) return;
    const mq = window.matchMedia('(min-width: 1024px)');
    const onChange = () => mq.matches && setOpen(false);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, [open, setOpen]);

  const menuButton = (
    <Button
      variant="ghost"
      size="icon"
      className="lg:hidden"
      aria-label={menuLabel}
      aria-expanded={open}
      onClick={() => setOpen(true)}
    >
      <Menu className="h-5 w-5" aria-hidden />
    </Button>
  );

  return (
    <div className="flex h-dvh overflow-hidden">
      <aside className={cn('hidden shrink-0 lg:block')}>{sidebar({ inDrawer: false })}</aside>

      <Sheet open={open} onOpenChange={setOpen}>
        <SheetContent side="left" width="w-64" className="p-0 lg:hidden" hideClose>
          {/* Radix requires a title on every Dialog; the drawer's is for screen readers only. */}
          <SheetTitle className="sr-only">{navLabel}</SheetTitle>
          {sidebar({ inDrawer: true })}
        </SheetContent>
      </Sheet>

      <div className="flex min-w-0 flex-1 flex-col">
        {banner}
        {topbar({ menuButton })}
        <main className="min-h-0 flex-1 overflow-y-auto">{children}</main>
      </div>
    </div>
  );
}
