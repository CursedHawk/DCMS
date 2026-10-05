import { ChevronDown, ChevronRight } from 'lucide-react';
import { useState, type ReactNode } from 'react';

const KEY = 'dcms.visual.inspector.closed';

function closedGroups(): Set<string> {
  try {
    return new Set(JSON.parse(localStorage.getItem(KEY) ?? '[]') as string[]);
  } catch {
    return new Set();
  }
}

/**
 * One group of the inspector (Content, Style, Layout…). Open unless the author closed it, and
 * remembered per browser: someone who never touches Advanced should not have to close it on
 * every selection.
 */
export function Group({ id, title, children }: { id: string; title: string; children: ReactNode }) {
  const [open, setOpen] = useState(() => !closedGroups().has(id));
  const toggle = () => {
    setOpen(!open);
    try {
      const closed = closedGroups();
      if (open) closed.add(id);
      else closed.delete(id);
      localStorage.setItem(KEY, JSON.stringify([...closed]));
    } catch {
      // A per-browser convenience; without storage the group simply opens next time.
    }
  };
  return (
    <section className="border-b last:border-b-0">
      <h3>
        <button
          type="button"
          aria-expanded={open}
          onClick={toggle}
          className="flex w-full items-center gap-1 px-4 py-2 text-left text-xs font-semibold uppercase tracking-wide text-muted-foreground hover:text-foreground"
        >
          {open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
          {title}
        </button>
      </h3>
      {open && <div className="space-y-4 px-4 pb-4">{children}</div>}
    </section>
  );
}
