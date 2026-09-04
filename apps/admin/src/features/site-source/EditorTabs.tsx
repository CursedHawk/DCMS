import { GitCompare, X } from 'lucide-react';
import { cn } from '@dcms/admin-ui';
import { useVfs } from './vfs';

// Open-editor tab strip: file tabs plus diff tabs (branch HEAD vs draft). The
// active tab drives whether a Monaco editor or a diff editor is shown.

function baseName(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

export function EditorTabs() {
  const openTabs = useVfs((s) => s.openTabs);
  const activePath = useVfs((s) => s.activePath);
  const activeDiff = useVfs((s) => s.activeDiff);
  const openDiffs = useVfs((s) => s.openDiffs);
  const setActive = useVfs((s) => s.setActive);
  const setActiveDiff = useVfs((s) => s.setActiveDiff);
  const closeTab = useVfs((s) => s.closeTab);
  const closeDiff = useVfs((s) => s.closeDiff);

  if (openTabs.length === 0 && openDiffs.length === 0) return <div className="h-9 border-b bg-card" />;

  return (
    <div className="flex h-9 shrink-0 items-stretch overflow-x-auto border-b bg-card">
      {openTabs.map((path) => {
        const active = !activeDiff && path === activePath;
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
              onClick={() => closeTab(path)}
              className="opacity-0 transition-opacity group-hover:opacity-100 hover:text-foreground"
              title="Close"
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
