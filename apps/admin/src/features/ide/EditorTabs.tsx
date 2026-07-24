import { X } from 'lucide-react';
import { cn } from '../../lib/cn';
import { useVfs } from './vfs';

// Open-editor tab strip. Names are the file basenames; the active tab drives
// which Monaco model is shown.

export function EditorTabs() {
  const openTabs = useVfs((s) => s.openTabs);
  const activePath = useVfs((s) => s.activePath);
  const setActive = useVfs((s) => s.setActive);
  const closeTab = useVfs((s) => s.closeTab);

  if (openTabs.length === 0) return <div className="h-9 border-b bg-card" />;

  return (
    <div className="flex h-9 shrink-0 items-stretch overflow-x-auto border-b bg-card">
      {openTabs.map((path) => {
        const name = path.slice(path.lastIndexOf('/') + 1);
        const active = path === activePath;
        return (
          <div
            key={path}
            className={cn(
              'group flex items-center gap-2 border-r px-3 text-xs',
              active ? 'bg-background text-foreground' : 'text-muted-foreground hover:bg-background/50',
            )}
          >
            <button type="button" onClick={() => setActive(path)} title={path}>
              {name}
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
    </div>
  );
}
