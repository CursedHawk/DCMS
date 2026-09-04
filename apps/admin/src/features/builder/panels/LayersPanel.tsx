import type { Component, Editor } from 'grapesjs';
import { ChevronDown, ChevronRight, Eye, EyeOff, Lock, LockOpen, Trash2 } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/admin-ui';
import { useEditorEvent } from './useEditorEvent';

/**
 * The layer tree.
 *
 * Reads the component tree straight from GrapesJS rather than mirroring it into
 * React state: the canvas is the source of truth, and any copy would drift the
 * moment a component is dropped, dragged or deleted from the canvas instead of
 * from here.
 */
export function LayersPanel({ editor }: { editor: Editor | null }) {
  const { t } = useTranslation();
  // Any structural or selection change invalidates the rendered tree.
  useEditorEvent(
    editor,
    'component:add component:remove component:update component:toggled component:mount update',
  );
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());

  const root = editor?.getWrapper();
  const children = useMemo(() => (root ? components(root) : []), [root, editor]);

  if (!editor || !root) {
    return <div className="p-4 text-sm text-muted-foreground">{t('common.loading')}</div>;
  }

  const toggle = (id: string) =>
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  return (
    <div className="flex h-full flex-col">
      <div className="border-b px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
        {t('builder.layers')}
      </div>
      <div className="min-h-0 flex-1 overflow-auto py-1">
        {children.length === 0 ? (
          <p className="px-3 py-2 text-sm text-muted-foreground">{t('builder.emptyPage')}</p>
        ) : (
          children.map((child) => (
            <LayerRow
              key={cid(child)}
              editor={editor}
              component={child}
              depth={0}
              collapsed={collapsed}
              onToggle={toggle}
            />
          ))
        )}
      </div>
    </div>
  );
}

function LayerRow({
  editor,
  component,
  depth,
  collapsed,
  onToggle,
}: {
  editor: Editor;
  component: Component;
  depth: number;
  collapsed: Set<string>;
  onToggle: (id: string) => void;
}) {
  const { t } = useTranslation();
  const id = cid(component);
  const children = components(component);
  const isCollapsed = collapsed.has(id);
  const selected = editor.getSelected() === component;
  const hidden = component.getStyle()?.display === 'none';
  const locked = component.get('locked') === true;

  return (
    <>
      <div
        className={cn(
          'group flex items-center gap-1 py-1 pr-2 text-sm',
          selected ? 'bg-accent text-accent-foreground' : 'hover:bg-accent/50',
        )}
        style={{ paddingLeft: `${depth * 12 + 8}px` }}
        // Hovering a layer outlines the component in the canvas, which is the
        // only way to tell two similar rows apart in a deep tree.
        onMouseEnter={() => setHovered(editor, component)}
        onMouseLeave={() => setHovered(editor, undefined)}
      >
        <button
          type="button"
          className={cn('shrink-0 rounded p-0.5', children.length === 0 && 'invisible')}
          onClick={() => onToggle(id)}
          aria-label={isCollapsed ? t('builder.expand') : t('builder.collapse')}
        >
          {isCollapsed ? <ChevronRight className="h-3.5 w-3.5" /> : <ChevronDown className="h-3.5 w-3.5" />}
        </button>

        <button
          type="button"
          className="min-w-0 flex-1 truncate text-left"
          onClick={() => editor.select(component)}
        >
          {component.getName()}
        </button>

        <button
          type="button"
          title={hidden ? t('builder.show') : t('builder.hide')}
          className={cn(
            'shrink-0 rounded p-1 text-muted-foreground',
            hidden ? 'opacity-100' : 'opacity-0 group-hover:opacity-100',
          )}
          onClick={() => component.addStyle({ display: hidden ? '' : 'none' })}
        >
          {hidden ? <EyeOff className="h-3.5 w-3.5" /> : <Eye className="h-3.5 w-3.5" />}
        </button>
        <button
          type="button"
          title={locked ? t('builder.unlock') : t('builder.lock')}
          className={cn(
            'shrink-0 rounded p-1 text-muted-foreground',
            locked ? 'opacity-100' : 'opacity-0 group-hover:opacity-100',
          )}
          onClick={() => component.set('locked', !locked)}
        >
          {locked ? <Lock className="h-3.5 w-3.5" /> : <LockOpen className="h-3.5 w-3.5" />}
        </button>
        <button
          type="button"
          title={t('actions.delete')}
          className="shrink-0 rounded p-1 text-muted-foreground opacity-0 hover:text-destructive group-hover:opacity-100"
          onClick={() => component.remove()}
        >
          <Trash2 className="h-3.5 w-3.5" />
        </button>
      </div>

      {!isCollapsed &&
        children.map((child) => (
          <LayerRow
            key={cid(child)}
            editor={editor}
            component={child}
            depth={depth + 1}
            collapsed={collapsed}
            onToggle={onToggle}
          />
        ))}
    </>
  );
}

/**
 * Only element components are listed. Text nodes are children in the model but
 * not things a layer tree can usefully target — showing them turns every
 * paragraph into a two-row entry for no gain.
 */
function components(component: Component): Component[] {
  return component
    .components()
    .toArray()
    .filter((child) => child.get('type') !== 'textnode' && child.get('type') !== 'comment');
}

function cid(component: Component): string {
  return String(component.getId() ?? component.cid);
}

/**
 * Highlight a component in the canvas.
 *
 * GrapesJS exposes this through the `core:component-outline`-style hover state
 * rather than a typed Canvas method, so it is reached via the editor model's
 * `hovered` component — the same channel the canvas itself uses when the mouse
 * moves over an element.
 */
function setHovered(editor: Editor, component: Component | undefined): void {
  const em = (editor as unknown as { getModel?: () => { setHovered?: (c: unknown, o?: object) => void } }).getModel?.();
  em?.setHovered?.(component ?? null, { forceChange: true });
}
