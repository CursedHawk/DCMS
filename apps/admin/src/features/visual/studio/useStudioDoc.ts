import { componentPath, type TenantComponentDoc } from '@dcms/site-runtime';
import { toast } from 'sonner';
import { useVfs } from '../../site-source';
import { readComponent, updateComponent } from '../documents';
import { useVisual } from '../store';

export interface StudioDoc {
  name: string;
  version: number;
  doc: TenantComponentDoc;
  change: (fn: (d: TenantComponentDoc) => TenantComponentDoc) => void;
}

/** The component open in the studio, its document, and one way to change it. */
export function useStudioDoc(): StudioDoc | null {
  const target = useVisual((s) => s.target);
  // Re-read whenever the file changes (the canvas, the code view, an AI edit).
  useVfs((s) => (target?.kind === 'component' ? s.files[componentPath(target.name, target.version)] : undefined));
  if (target?.kind !== 'component') return null;
  const doc = readComponent(target.name, target.version);
  if (!doc) return null;
  return {
    name: target.name,
    version: target.version,
    doc,
    change: (fn) => {
      // Template edits still in the canvas's debounce go first, or this write would undo them.
      useVisual.getState().flushCanvas();
      const result = updateComponent(target.name, target.version, fn);
      if (!result.ok) toast.error(result.error);
    },
  };
}

