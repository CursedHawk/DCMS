import { type ComponentNode, effectiveLayout } from '@dcms/editor-core';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { cn } from '../../lib/cn';
import { BREAKPOINT_WIDTH, SNAP_THRESHOLD } from './constants';
import { NodeRenderer } from './renderer';
import { useEditor } from './store';

interface Box {
  x: number;
  y: number;
  w: number;
  h: number;
}
type Handle = 'nw' | 'n' | 'ne' | 'e' | 'se' | 's' | 'sw' | 'w';
const HANDLES: Handle[] = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'];

export function Canvas() {
  const page = useEditor((s) => s.currentPage());
  const breakpoint = useEditor((s) => s.breakpoint);
  const selectedId = useEditor((s) => s.selectedId);
  const select = useEditor((s) => s.select);
  const updateLayout = useEditor((s) => s.updateLayout);

  const wrapRef = useRef<HTMLDivElement>(null);
  const [scale, setScale] = useState(1);
  // Transient box while dragging/resizing (avoids one history entry per move).
  const [draft, setDraft] = useState<{ id: string; box: Box } | null>(null);
  const [guides, setGuides] = useState<{ v: number[]; h: number[] }>({ v: [], h: [] });

  const stageW = page?.canvas?.width ?? BREAKPOINT_WIDTH[breakpoint];
  const displayW = breakpoint === 'desktop' ? stageW : BREAKPOINT_WIDTH[breakpoint];
  const stageH = page?.canvas?.minHeight ?? 900;
  const nodes = page?.root.children ?? [];

  // Fit the stage to the available width.
  useLayoutEffect(() => {
    const measure = () => {
      const avail = (wrapRef.current?.clientWidth ?? displayW) - 48;
      setScale(Math.min(1, avail / displayW));
    };
    measure();
    window.addEventListener('resize', measure);
    return () => window.removeEventListener('resize', measure);
  }, [displayW]);

  function boxOf(node: ComponentNode): Box {
    if (draft && draft.id === node.id) return draft.box;
    const l = effectiveLayout(node, breakpoint) ?? { x: 0, y: 0, w: 200, h: 80 };
    return { x: l.x, y: l.y, w: l.w, h: l.h };
  }

  // Snap a moving/resizing box against sibling edges + canvas frame.
  function snap(box: Box, movingId: string, mode: 'move' | 'resize'): { box: Box; guides: { v: number[]; h: number[] } } {
    const thr = SNAP_THRESHOLD / scale;
    const others = nodes.filter((n) => n.id !== movingId).map(boxOf);
    const vTargets = [0, displayW / 2, displayW];
    const hTargets = [0, stageH / 2];
    for (const o of others) {
      vTargets.push(o.x, o.x + o.w / 2, o.x + o.w);
      hTargets.push(o.y, o.y + o.h / 2, o.y + o.h);
    }
    const vGuides: number[] = [];
    const hGuides: number[] = [];
    const out = { ...box };

    // Vertical (x) snapping using left / center / right edges.
    const xEdges = mode === 'move' ? [out.x, out.x + out.w / 2, out.x + out.w] : [out.x, out.x + out.w];
    for (const t of vTargets) {
      for (let i = 0; i < xEdges.length; i++) {
        if (Math.abs(xEdges[i] - t) <= thr) {
          const shift = t - xEdges[i];
          if (mode === 'move') out.x += shift;
          else if (i === 0) {
            out.w -= shift;
            out.x += shift;
          } else out.w += shift;
          vGuides.push(t);
        }
      }
    }
    const yEdges = mode === 'move' ? [out.y, out.y + out.h / 2, out.y + out.h] : [out.y, out.y + out.h];
    for (const t of hTargets) {
      for (let i = 0; i < yEdges.length; i++) {
        if (Math.abs(yEdges[i] - t) <= thr) {
          const shift = t - yEdges[i];
          if (mode === 'move') out.y += shift;
          else if (i === 0) {
            out.h -= shift;
            out.y += shift;
          } else out.h += shift;
          hGuides.push(t);
        }
      }
    }
    return { box: out, guides: { v: [...new Set(vGuides)], h: [...new Set(hGuides)] } };
  }

  function startDrag(e: React.PointerEvent, node: ComponentNode) {
    e.stopPropagation();
    select(node.id);
    const start = boxOf(node);
    const sx = e.clientX;
    const sy = e.clientY;
    (e.target as HTMLElement).setPointerCapture(e.pointerId);

    const onMove = (ev: PointerEvent) => {
      const dx = (ev.clientX - sx) / scale;
      const dy = (ev.clientY - sy) / scale;
      const moved = { ...start, x: Math.round(start.x + dx), y: Math.round(start.y + dy) };
      const snapped = snap(moved, node.id, 'move');
      setDraft({ id: node.id, box: snapped.box });
      setGuides(snapped.guides);
    };
    const onUp = () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      setDraft((d) => {
        if (d) updateLayout(node.id, d.box);
        return null;
      });
      setGuides({ v: [], h: [] });
    };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
  }

  function startResize(e: React.PointerEvent, node: ComponentNode, handle: Handle) {
    e.stopPropagation();
    e.preventDefault();
    const start = boxOf(node);
    const sx = e.clientX;
    const sy = e.clientY;

    const onMove = (ev: PointerEvent) => {
      const dx = (ev.clientX - sx) / scale;
      const dy = (ev.clientY - sy) / scale;
      let { x, y, w, h } = start;
      if (handle.includes('e')) w = start.w + dx;
      if (handle.includes('s')) h = start.h + dy;
      if (handle.includes('w')) {
        w = start.w - dx;
        x = start.x + dx;
      }
      if (handle.includes('n')) {
        h = start.h - dy;
        y = start.y + dy;
      }
      w = Math.max(24, Math.round(w));
      h = Math.max(24, Math.round(h));
      const snapped = snap({ x: Math.round(x), y: Math.round(y), w, h }, node.id, 'resize');
      setDraft({ id: node.id, box: snapped.box });
      setGuides(snapped.guides);
    };
    const onUp = () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      setDraft((d) => {
        if (d) updateLayout(node.id, d.box);
        return null;
      });
      setGuides({ v: [], h: [] });
    };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
  }

  // Keyboard: delete + arrow nudge.
  const removeSelected = useEditor((s) => s.removeSelected);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!selectedId) return;
      const tag = (e.target as HTMLElement).tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA') return;
      if (e.key === 'Delete' || e.key === 'Backspace') {
        e.preventDefault();
        removeSelected();
      } else if (e.key.startsWith('Arrow')) {
        e.preventDefault();
        const step = e.shiftKey ? 10 : 1;
        const node = nodes.find((n) => n.id === selectedId);
        if (!node) return;
        const b = boxOf(node);
        if (e.key === 'ArrowLeft') updateLayout(selectedId, { x: b.x - step });
        if (e.key === 'ArrowRight') updateLayout(selectedId, { x: b.x + step });
        if (e.key === 'ArrowUp') updateLayout(selectedId, { y: b.y - step });
        if (e.key === 'ArrowDown') updateLayout(selectedId, { y: b.y + step });
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedId, nodes]);

  if (!page) {
    return <div className="flex flex-1 items-center justify-center text-muted-foreground">No page</div>;
  }

  return (
    <div ref={wrapRef} className="flex-1 overflow-auto bg-muted/30 p-6" onPointerDown={() => select(null)}>
      <div className="mx-auto" style={{ width: displayW * scale, height: stageH * scale }}>
        <div
          className="relative origin-top-left bg-background shadow-xl ring-1 ring-border"
          style={{ width: displayW, height: stageH, transform: `scale(${scale})` }}
          onPointerDown={(e) => e.stopPropagation()}
        >
          {nodes
            .slice()
            .sort((a, b) => (a.layout?.z ?? 0) - (b.layout?.z ?? 0))
            .map((node) => {
              const b = boxOf(node);
              const isSel = node.id === selectedId;
              return (
                <div
                  key={node.id}
                  onPointerDown={(e) => startDrag(e, node)}
                  className={cn(
                    'absolute cursor-move select-none',
                    isSel ? 'ring-2 ring-primary' : 'hover:ring-1 hover:ring-primary/50',
                  )}
                  style={{ left: b.x, top: b.y, width: b.w, height: b.h, zIndex: node.layout?.z ?? 0 }}
                >
                  <NodeRenderer node={node} />
                  {isSel
                    ? HANDLES.map((h) => (
                        <span
                          key={h}
                          onPointerDown={(e) => startResize(e, node, h)}
                          className="absolute z-10 h-2.5 w-2.5 rounded-full border border-primary bg-background"
                          style={handleStyle(h)}
                        />
                      ))
                    : null}
                </div>
              );
            })}

          {/* Snap guides */}
          {guides.v.map((x, i) => (
            <div key={`v${i}`} className="pointer-events-none absolute top-0 z-50 h-full w-px bg-primary" style={{ left: x }} />
          ))}
          {guides.h.map((y, i) => (
            <div key={`h${i}`} className="pointer-events-none absolute left-0 z-50 h-px w-full bg-primary" style={{ top: y }} />
          ))}
        </div>
      </div>
    </div>
  );
}

function handleStyle(h: Handle): React.CSSProperties {
  const c = -5;
  const mid = 'calc(50% - 5px)';
  const map: Record<Handle, React.CSSProperties> = {
    nw: { left: c, top: c, cursor: 'nwse-resize' },
    n: { left: mid, top: c, cursor: 'ns-resize' },
    ne: { right: c, top: c, cursor: 'nesw-resize' },
    e: { right: c, top: mid, cursor: 'ew-resize' },
    se: { right: c, bottom: c, cursor: 'nwse-resize' },
    s: { left: mid, bottom: c, cursor: 'ns-resize' },
    sw: { left: c, bottom: c, cursor: 'nesw-resize' },
    w: { left: c, top: mid, cursor: 'ew-resize' },
  };
  return map[h];
}
