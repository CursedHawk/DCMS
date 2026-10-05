import type { Registry } from '@dcms/site-runtime';
import type { Editor } from 'grapesjs';
import { ChevronRight, MousePointerClick } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@dcms/ui';
import { useSelected } from '../builder/panels/useEditorEvent';
import { modifierLabel } from '../ide/commands';
import { ShortcutSheet } from '../ide/ShortcutSheet';
import { SHOW_SHORTCUTS } from './canvas/commands';
import { canPasteStyle, copyStyle, duplicate, findNode, move, pasteStyle, remove, selectParent, trail, unwrap, wrap, type Outcome } from './canvas/operations';
import { canMakeReusable, makeReusable } from './makeReusable';
import { useVisual } from './store';

/**
 * Where the selection sits, as clickable steps (Page › Section › Stack › Heading): the way to
 * reach a container whose children cover it on the canvas.
 */
export function SelectionTrail({ editor, registry }: { editor: Editor | null; registry: Registry }) {
  const { t } = useTranslation();
  const selected = useSelected(editor);
  const steps = trail(selected);
  return (
    <nav aria-label={t('visual.trail.label')} className="flex h-8 min-w-0 shrink-0 items-center gap-0.5 overflow-x-auto border-b bg-card px-2 text-xs">
      {steps.length === 0 ? (
        <span className="flex items-center gap-1.5 text-muted-foreground">
          <MousePointerClick className="h-3.5 w-3.5" /> {t('visual.trail.empty')}
        </span>
      ) : (
        steps.map((c, i) => {
          const last = i === steps.length - 1;
          const label = registry.get(c.get('type') as string)?.label ?? c.getName();
          return (
            <span key={c.cid} className="flex shrink-0 items-center gap-0.5">
              {i > 0 && <ChevronRight className="h-3 w-3 text-muted-foreground" />}
              <button
                type="button"
                aria-current={last ? 'true' : undefined}
                disabled={c.get('selectable') === false}
                onClick={() => editor?.select(c)}
                className={last ? 'rounded px-1.5 py-0.5 font-medium' : 'rounded px-1.5 py-0.5 text-muted-foreground hover:bg-accent hover:text-foreground'}
              >
                {label}
              </button>
            </span>
          );
        })
      )}
    </nav>
  );
}

/** Containers a selection can be wrapped in, in the order the menu offers them. */
const WRAPPERS = ['dcms.section', 'dcms.container', 'dcms.stack', 'dcms.card'];

/**
 * Right-click on the canvas: what can be done to that part, with the keys that do it. Picks
 * the part under the pointer first, so the menu always acts on what was clicked.
 */
export function CanvasMenu({ editor, registry }: { editor: Editor | null; registry: Registry }) {
  const { t } = useTranslation();
  const [at, setAt] = useState<{ x: number; y: number } | null>(null);
  const selected = useSelected(editor);

  useEffect(() => {
    if (!editor) return;
    let doc: Document | null = null;
    const onMenu = (e: MouseEvent) => {
      const el = (e.target as Element | null)?.closest?.('[data-dcms-node]');
      const component = el && findNode(editor, el.getAttribute('data-dcms-node')!);
      if (!component || component.get('selectable') === false) return;
      e.preventDefault();
      editor.select(component);
      const frame = editor.Canvas.getFrameEl()?.getBoundingClientRect();
      setAt({ x: (frame?.left ?? 0) + e.clientX, y: (frame?.top ?? 0) + e.clientY });
    };
    const attach = () => {
      doc?.removeEventListener('contextmenu', onMenu);
      doc = editor.Canvas.getDocument() ?? null;
      doc?.addEventListener('contextmenu', onMenu);
    };
    attach();
    editor.on('load canvas:frame:load', attach);
    return () => {
      editor.off('load canvas:frame:load', attach);
      doc?.removeEventListener('contextmenu', onMenu);
    };
  }, [editor]);

  if (!editor) return null;
  const run = (out: Outcome) => {
    if (!out.ok) toast.error(out.reason);
  };
  const mod = modifierLabel();
  const item = (label: string, onSelect: () => void, keys?: string, disabled = false) => (
    <DropdownMenuItem onSelect={onSelect} disabled={disabled} className="justify-between gap-6">
      <span>{label}</span>
      {keys && <kbd className="font-mono text-[10px] text-muted-foreground">{keys}</kbd>}
    </DropdownMenuItem>
  );
  const name = selected ? (registry.get(selected.get('type') as string)?.label ?? selected.getName()) : '';

  return (
    <DropdownMenu open={!!at} onOpenChange={(open) => !open && setAt(null)} modal={false}>
      <DropdownMenuTrigger asChild>
        <span aria-hidden className="pointer-events-none fixed h-px w-px" style={{ left: at?.x ?? 0, top: at?.y ?? 0 }} />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" sideOffset={2} aria-label={t('visual.menu.label', { name })} className="min-w-56">
        {item(t('visual.menu.selectParent'), () => run(selectParent(editor)), 'Esc')}
        <DropdownMenuSeparator />
        {item(t('visual.menu.duplicate'), () => run(duplicate(editor, registry)), `${mod} D`)}
        {item(t('visual.menu.moveUp'), () => run(move(editor, -1)), 'Alt ↑')}
        {item(t('visual.menu.moveDown'), () => run(move(editor, 1)), 'Alt ↓')}
        <DropdownMenuSeparator />
        {WRAPPERS.map((type) => (
          <DropdownMenuItem key={type} onSelect={() => run(wrap(editor, registry, type))}>
            {t('visual.menu.wrapIn', { name: registry.get(type)?.label ?? type })}
          </DropdownMenuItem>
        ))}
        {item(t('visual.menu.unwrap'), () => run(unwrap(editor, registry)), undefined, !selected?.components().models.some((s) => s.components().length > 0))}
        <DropdownMenuSeparator />
        {item(t('visual.menu.copyStyle'), () => run(copyStyle(editor, registry)), `${mod} Alt C`)}
        {item(t('visual.menu.pasteStyle'), () => run(pasteStyle(editor, registry)), `${mod} Alt V`, !canPasteStyle())}
        {item(t('visual.menu.makeReusable'), () => canMakeReusable(selected) && makeReusable(selected, t), undefined, !canMakeReusable(selected))}
        <DropdownMenuSeparator />
        {item(t('visual.menu.delete'), () => run(remove(editor)), 'Del')}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** The canvas's keys, opened with `?` on the canvas. */
export function CanvasShortcuts({ editor }: { editor: Editor | null }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  useEffect(() => {
    if (!editor) return;
    const show = () => setOpen(true);
    editor.on(SHOW_SHORTCUTS, show);
    return () => {
      editor.off(SHOW_SHORTCUTS, show);
    };
  }, [editor]);
  const mod = modifierLabel();
  return (
    <ShortcutSheet
      open={open}
      onOpenChange={setOpen}
      groups={[
        {
          title: t('visual.shortcuts.select'),
          rows: [
            { keys: 'Esc', label: t('visual.menu.selectParent') },
            { keys: t('visual.shortcuts.rightClick'), label: t('visual.shortcuts.menu') },
          ],
        },
        {
          title: t('visual.shortcuts.change'),
          rows: [
            { keys: `${mod} D`, label: t('visual.menu.duplicate') },
            { keys: 'Alt ↑ / Alt ↓', label: t('visual.shortcuts.move') },
            { keys: 'Del', label: t('visual.menu.delete') },
            { keys: `${mod} C / ${mod} V`, label: t('visual.shortcuts.copyPaste') },
            { keys: `${mod} Alt C / V`, label: t('visual.shortcuts.style') },
            { keys: `${mod} Z / ${mod} ⇧ Z`, label: t('visual.shortcuts.undo') },
          ],
        },
        { title: t('ide.shortcuts.help'), rows: [{ keys: '?', label: t('ide.shortcuts.title') }] },
      ]}
    />
  );
}

/**
 * While a part is dragged: where it would land, or — in red — why it cannot go where the pointer
 * is, so a drop that does not happen is never a mystery.
 */
export function DragNote({ editor }: { editor: Editor | null }) {
  const note = useVisual((s) => s.dragNote);
  useEffect(() => {
    if (!editor) return;
    const clear = () => useVisual.setState({ dragNote: null });
    const events = 'block:drag:start block:drag:stop component:drag:start component:drag:end';
    editor.on(events, clear);
    return () => {
      editor.off(events, clear);
    };
  }, [editor]);
  if (!note) return null;
  return (
    <div
      role="status"
      className={
        note.ok
          ? 'pointer-events-none absolute bottom-3 left-1/2 z-10 -translate-x-1/2 rounded-full bg-primary px-3 py-1 text-xs text-primary-foreground shadow'
          : 'pointer-events-none absolute bottom-3 left-1/2 z-10 -translate-x-1/2 rounded-full bg-destructive px-3 py-1 text-xs text-destructive-foreground shadow'
      }
    >
      {note.text}
    </div>
  );
}
