import { GitCompare, Pin, X } from 'lucide-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';
import { useVfs } from './vfs';

// Open-editor tab strip: file tabs plus diff tabs (branch HEAD vs draft). The
// active tab drives whether a Monaco editor or a diff editor is shown.

function baseName(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

export function EditorTabs() {
  const { t } = useTranslation();
  const openTabs = useVfs((s) => s.openTabs);
  const pinnedTabs = useVfs((s) => s.pinnedTabs);
  const activePath = useVfs((s) => s.activePath);
  const activeDiff = useVfs((s) => s.activeDiff);
  const openDiffs = useVfs((s) => s.openDiffs);
  const setActive = useVfs((s) => s.setActive);
  const setActiveDiff = useVfs((s) => s.setActiveDiff);
  const closeTab = useVfs((s) => s.closeTab);
  const closeDiff = useVfs((s) => s.closeDiff);
  const togglePin = useVfs((s) => s.togglePin);

  /*
   * Pinned tabs sort to the front.
   *
   * Their whole job is to still be there after twenty other files have come and gone — which
   * they are not if they stay in open-order and get pushed off the left edge of a strip that
   * scrolls. Order within each group is preserved, so nothing else moves.
   */
  const ordered = useMemo(() => {
    const pinned = openTabs.filter((p) => pinnedTabs.includes(p));
    const rest = openTabs.filter((p) => !pinnedTabs.includes(p));
    return [...pinned, ...rest];
  }, [openTabs, pinnedTabs]);

  if (openTabs.length === 0 && openDiffs.length === 0) return <div className="h-9 border-b bg-card" />;

  return (
    <div className="flex h-9 shrink-0 items-stretch overflow-x-auto border-b bg-card">
      {ordered.map((path) => {
        const active = !activeDiff && path === activePath;
        const pinned = pinnedTabs.includes(path);
        return (
          <div
            key={path}
            className={cn(
              'group flex items-center gap-2 border-r px-3 text-xs',
              active ? 'bg-background text-foreground' : 'text-muted-foreground hover:bg-background/50',
            )}
          >
            <button type="button" onClick={() => setActive(path)} title={path}>
              {baseName(path)}
            </button>
            <button
              type="button"
              onClick={() => togglePin(path)}
              aria-pressed={pinned}
              title={pinned ? t('ide.tabs.unpin') : t('ide.tabs.pin')}
              className={cn(
                'transition-opacity hover:text-foreground',
                // A pinned tab shows its pin always — that is the state it is advertising.
                // An unpinned one only offers the action on hover or keyboard focus.
                pinned ? 'text-primary' : 'opacity-0 focus-visible:opacity-100 group-hover:opacity-100',
              )}
            >
              <Pin className={cn('h-3 w-3', pinned && 'fill-current')} aria-hidden />
            </button>
            <button
              type="button"
              onClick={() => closeTab(path)}
              className="opacity-0 transition-opacity focus-visible:opacity-100 group-hover:opacity-100 hover:text-foreground"
              title={t('ide.tabs.close')}
            >
              <X className="h-3 w-3" />
            </button>
          </div>
        );
      })}
      {openDiffs.map((d) => {
        const active = activeDiff === d.path;
        return (
          <div
            key={`diff:${d.path}`}
            className={cn(
              'group flex items-center gap-2 border-r px-3 text-xs',
              active ? 'bg-background text-foreground' : 'text-muted-foreground hover:bg-background/50',
            )}
          >
            <button
              type="button"
              onClick={() => setActiveDiff(d.path)}
              title={`${d.path} (diff)`}
              className="flex items-center gap-1.5"
            >
              <GitCompare className="h-3 w-3 shrink-0" />
              {baseName(d.path)}
            </button>
            <button
              type="button"
              onClick={() => closeDiff(d.path)}
              className="opacity-0 transition-opacity group-hover:opacity-100 hover:text-foreground"
              title="Close"
            >
              <X className="h-3 w-3" />
            </button>
          </div>
        );
      })}
    </div>
  );
}
