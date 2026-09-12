import { useCallback, useEffect, useState } from 'react';

/**
 * Which bottom-panel tab is showing, and whether the panel is open at all.
 *
 * <p><b>Why the panel exists.</b> Problems, the preview's console, the build history and the
 * workspace log were either buried in the left sidebar or nowhere. All four are about the code
 * in the editor and all four are wide and short — a list of `file:line  message` rows reads
 * across, not down — so a sidebar was the wrong shape for every one of them. They now share the
 * space under the editor, where what they describe is visible at the same time.</p>
 *
 * <p>Stored per browser rather than per site: which tab you keep open is a working habit, not a
 * property of the site you happen to have open.</p>
 */

export const PANEL_TABS = ['problems', 'output', 'console', 'build'] as const;

export type PanelTab = (typeof PANEL_TABS)[number];

const OPEN_KEY = 'dcms.ide.panel.open';
const TAB_KEY = 'dcms.ide.panel.tab';

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Quota, or storage disabled. A forgotten panel position is not worth an error.
  }
}

function isTab(value: string | null): value is PanelTab {
  return (PANEL_TABS as readonly string[]).includes(value ?? '');
}

export interface PanelState {
  open: boolean;
  tab: PanelTab;
  /** Open the panel on a tab, or toggle it closed if that tab is already showing. */
  toggle: (tab?: PanelTab) => void;
  /** Open on a specific tab. Used by anything that wants to *show* something. */
  show: (tab: PanelTab) => void;
  close: () => void;
}

export function usePanelState(): PanelState {
  const [open, setOpen] = useState(() => read(OPEN_KEY) === '1');
  const [tab, setTab] = useState<PanelTab>(() => {
    const stored = read(TAB_KEY);
    return isTab(stored) ? stored : 'problems';
  });

  useEffect(() => write(OPEN_KEY, open ? '1' : '0'), [open]);
  useEffect(() => write(TAB_KEY, tab), [tab]);

  const show = useCallback((next: PanelTab) => {
    setTab(next);
    setOpen(true);
  }, []);

  const close = useCallback(() => setOpen(false), []);

  const toggle = useCallback(
    (next?: PanelTab) => {
      if (!next) {
        setOpen((v) => !v);
        return;
      }
      // Clicking the tab you are already on closes the panel — how every editor with a bottom
      // panel behaves, and the only way to dismiss it without reaching for the chevron.
      if (open && tab === next) {
        setOpen(false);
        return;
      }
      setTab(next);
      setOpen(true);
    },
    [open, tab],
  );

  return { open, tab, toggle, show, close };
}
