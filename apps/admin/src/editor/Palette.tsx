import { availableComponents, type ComponentRegistration } from '@dcms/site-components';
import { useDraggable } from '@dnd-kit/core';
import { useEditor } from './store';

function PaletteItem({ reg }: { reg: ComponentRegistration }) {
  const { attributes, listeners, setNodeRef, isDragging } = useDraggable({
    id: `palette:${reg.type}`,
    data: { kind: 'palette', type: reg.type },
  });
  const addComponent = useEditor((s) => s.addComponent);
  const selectedId = useEditor((s) => s.selectedId);
  const root = useEditor((s) => s.currentRoot());

  return (
    <button
      ref={setNodeRef}
      type="button"
      {...listeners}
      {...attributes}
      // Click-to-add into the selected container (or the page root) as a reliable
      // alternative to dragging.
      onClick={() => addComponent(reg.type, selectedId ?? root?.id ?? '')}
      className="w-full rounded border border-slate-200 px-2 py-1 text-left text-sm hover:bg-slate-100"
      style={{ opacity: isDragging ? 0.5 : 1 }}
    >
      {reg.displayName}
      {reg.requiredPluginId ? <span className="ml-1 text-xs text-slate-400">({reg.requiredPluginId})</span> : null}
    </button>
  );
}

export function Palette({ enabledPlugins }: { enabledPlugins: ReadonlySet<string> }) {
  const components = availableComponents(enabledPlugins);
  const groups = ['layout', 'content', 'plugin'] as const;
  return (
    <aside className="w-56 shrink-0 space-y-4 overflow-y-auto border-r border-slate-200 p-3">
      {groups.map((group) => (
        <div key={group}>
          <h3 className="mb-1 text-xs font-semibold uppercase text-slate-400">{group}</h3>
          <div className="space-y-1">
            {components.filter((c) => c.category === group).map((c) => (
              <PaletteItem key={c.type} reg={c} />
            ))}
          </div>
        </div>
      ))}
    </aside>
  );
}
