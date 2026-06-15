import { DndContext, type DragEndEvent, PointerSensor, useSensor, useSensors } from '@dnd-kit/core';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { pluginsApi, sitesApi } from './api';
import { Canvas } from './Canvas';
import { Inspector } from './Inspector';
import { Palette } from './Palette';
import { useEditor } from './store';

export function EditorPage({ siteId }: { siteId: string }) {
  const editor = useEditor();
  const [status, setStatus] = useState('');
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }));

  const site = useQuery({ queryKey: ['site', siteId], queryFn: () => sitesApi.get(siteId) });
  const instances = useQuery({ queryKey: ['instances'], queryFn: () => pluginsApi.instances() });

  useEffect(() => {
    if (site.data) editor.load(site.data.definition);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [site.data]);

  const enabledPlugins = new Set((instances.data ?? []).filter((i) => i.enabled).map((i) => i.pluginId));

  function onDragEnd(e: DragEndEvent) {
    const data = e.active.data.current as { kind?: string; type?: string } | undefined;
    const overId = e.over?.id as string | undefined;
    if (data?.kind === 'palette' && data.type && overId) {
      editor.addComponent(data.type, overId);
    }
  }

  async function save() {
    setStatus('Saving…');
    await sitesApi.saveDefinition(siteId, editor.definition());
    editor.markSaved();
    setStatus('Saved');
  }

  async function publish() {
    await save();
    setStatus('Publishing…');
    await sitesApi.publish(siteId);
    setStatus('Publish queued');
  }

  if (site.isLoading) return <p className="p-6">Loading editor…</p>;

  return (
    <DndContext sensors={sensors} onDragEnd={onDragEnd}>
      <div className="flex h-[calc(100vh-7rem)] flex-col">
        <div className="flex items-center gap-2 border-b border-slate-200 bg-white px-3 py-2 text-sm">
          <strong>{site.data?.name}</strong>
          <span className="flex-1" />
          <PageSelect />
          <button type="button" disabled={!editor.canUndo()} onClick={editor.undo} className="rounded border px-2 py-1 disabled:opacity-40">Undo</button>
          <button type="button" disabled={!editor.canRedo()} onClick={editor.redo} className="rounded border px-2 py-1 disabled:opacity-40">Redo</button>
          <button type="button" onClick={save} className="rounded bg-slate-200 px-3 py-1">Save</button>
          <button type="button" onClick={publish} className="rounded bg-slate-900 px-3 py-1 text-white">Publish</button>
          <span className="w-28 text-right text-xs text-slate-500">{editor.dirty ? 'Unsaved' : status}</span>
        </div>
        <div className="flex flex-1 overflow-hidden">
          <Palette enabledPlugins={enabledPlugins} />
          <Canvas />
          <Inspector instances={instances.data ?? []} />
        </div>
      </div>
    </DndContext>
  );
}

function PageSelect() {
  const def = useEditor((s) => s.history.present);
  const pageId = useEditor((s) => s.pageId);
  const selectPage = useEditor((s) => s.selectPage);
  if (def.pages.length <= 1) return null;
  return (
    <select value={pageId ?? ''} onChange={(e) => selectPage(e.target.value)} className="rounded border px-2 py-1">
      {def.pages.map((p) => <option key={p.id} value={p.id}>{p.title || p.path}</option>)}
    </select>
  );
}
