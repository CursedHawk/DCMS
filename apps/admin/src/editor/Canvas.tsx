import type { ComponentNode } from '@dcms/editor-core';
import { findRegistration } from '@dcms/site-components';
import { useDroppable } from '@dnd-kit/core';
import { useEditor } from './store';

function NodeView({ node }: { node: ComponentNode }) {
  const selectedId = useEditor((s) => s.selectedId);
  const select = useEditor((s) => s.select);
  const reg = findRegistration(node.type);
  const acceptsChildren = reg?.acceptsChildren ?? false;
  const { setNodeRef, isOver } = useDroppable({ id: node.id, data: { kind: 'node', nodeId: node.id }, disabled: !acceptsChildren });
  const isSelected = node.id === selectedId;
  const bound = (node.bindings?.length ?? 0) > 0;

  return (
    <div
      ref={acceptsChildren ? setNodeRef : undefined}
      onClick={(e) => {
        e.stopPropagation();
        select(node.id);
      }}
      className="rounded border p-2"
      style={{
        borderColor: isSelected ? '#2563eb' : isOver ? '#93c5fd' : '#e2e8f0',
        background: isOver ? '#eff6ff' : 'white',
        margin: '4px 0',
      }}
    >
      <div className="mb-1 text-xs text-slate-400">
        {reg?.displayName ?? node.type}
        {bound ? ' · bound' : ''}
      </div>
      <NodePreview node={node} />
      {acceptsChildren ? (
        <div className="ml-3 border-l border-dashed border-slate-200 pl-2">
          {(node.children ?? []).map((c) => (
            <NodeView key={c.id} node={c} />
          ))}
          {(node.children?.length ?? 0) === 0 ? (
            <div className="py-2 text-xs italic text-slate-300">Drop or add components here</div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

function NodePreview({ node }: { node: ComponentNode }) {
  const p = node.props as Record<string, string>;
  switch (node.type) {
    case 'Heading':
      return <strong>{p.text}</strong>;
    case 'Text':
      return <span className="text-sm text-slate-700">{p.text}</span>;
    case 'Hero':
      return <span className="text-sm font-semibold">{p.title}</span>;
    case 'Button':
      return <span className="text-sm text-blue-600 underline">{p.label}</span>;
    case 'Image':
      return <span className="text-xs text-slate-400">🖼 {p.src || '(no source)'}</span>;
    default:
      return null;
  }
}

export function Canvas() {
  const root = useEditor((s) => s.currentRoot());
  const select = useEditor((s) => s.select);
  if (!root) {
    return <div className="flex-1 p-6 text-slate-400">No page selected.</div>;
  }
  return (
    <main className="flex-1 overflow-y-auto bg-slate-50 p-6" onClick={() => select(null)}>
      <div className="mx-auto max-w-3xl rounded bg-white p-4 shadow">
        <NodeView node={root} />
      </div>
    </main>
  );
}
