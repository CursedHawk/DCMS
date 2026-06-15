import { findNode } from '@dcms/editor-core';
import { findRegistration } from '@dcms/site-components';
import type { PluginInstanceSummary } from './api';
import { useEditor } from './store';

interface SchemaProp { type?: string; title?: string; enum?: string[] }

export function Inspector({ instances }: { instances: PluginInstanceSummary[] }) {
  const selectedId = useEditor((s) => s.selectedId);
  const root = useEditor((s) => s.currentRoot());
  const updateProps = useEditor((s) => s.updateSelectedProps);
  const setBinding = useEditor((s) => s.setBinding);
  const removeSelected = useEditor((s) => s.removeSelected);

  if (!selectedId || !root) {
    return <aside className="w-72 shrink-0 border-l border-slate-200 p-3 text-sm text-slate-400">Select a component.</aside>;
  }
  const node = findNode(root, selectedId);
  const reg = node ? findRegistration(node.type) : undefined;
  if (!node || !reg) {
    return <aside className="w-72 shrink-0 border-l border-slate-200 p-3 text-sm text-slate-400">Unknown component.</aside>;
  }

  const props = (reg.propSchema.properties ?? {}) as Record<string, SchemaProp>;
  const values = node.props as Record<string, string>;

  return (
    <aside className="w-72 shrink-0 space-y-3 overflow-y-auto border-l border-slate-200 p-3">
      <div className="flex items-center justify-between">
        <h3 className="font-semibold">{reg.displayName}</h3>
        {selectedId !== root.id ? (
          <button type="button" onClick={removeSelected} className="text-xs text-red-600">Delete</button>
        ) : null}
      </div>

      {Object.entries(props).map(([name, schema]) => (
        <label key={name} className="block text-sm">
          <span className="text-slate-600">{schema.title ?? name}</span>
          {schema.enum ? (
            <select
              value={values[name] ?? ''}
              onChange={(e) => updateProps({ [name]: e.target.value })}
              className="mt-1 w-full rounded border border-slate-300 px-2 py-1"
            >
              {schema.enum.map((o) => <option key={o} value={o}>{o}</option>)}
            </select>
          ) : (
            <input
              value={values[name] ?? ''}
              onChange={(e) => updateProps({ [name]: e.target.value })}
              className="mt-1 w-full rounded border border-slate-300 px-2 py-1"
            />
          )}
        </label>
      ))}

      {reg.binding ? (
        <label className="block text-sm">
          <span className="text-slate-600">Data source ({reg.binding.contentType})</span>
          <select
            value={node.bindings?.[0]?.source.instanceSlug ?? ''}
            onChange={(e) => setBinding(node.id, reg.binding!.propPath, e.target.value)}
            className="mt-1 w-full rounded border border-slate-300 px-2 py-1"
          >
            <option value="">— none —</option>
            {instances
              .filter((i) => i.enabled && i.pluginId === reg.requiredPluginId)
              .map((i) => <option key={i.id} value={i.slug}>{i.name} ({i.slug})</option>)}
          </select>
        </label>
      ) : null}
    </aside>
  );
}
