import type { Editor } from 'grapesjs';
import { Search } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Input } from '../../../components/ui/input';
import { cn } from '../../../lib/cn';
import { useCustomPayload } from './useEditorEvent';

/**
 * The block palette.
 *
 * GrapesJS's BlockManager runs in `custom: true` mode and hands us the blocks
 * plus the three drag callbacks its own view would have used. Wiring those to
 * native HTML5 drag events is what makes a React-rendered tile droppable into
 * the canvas iframe — without them the tile would drag nowhere.
 */

interface BlockModel {
  getId: () => string;
  get: (key: string) => unknown;
  getLabel: () => string;
  getMedia: () => string | undefined;
  getCategoryLabel: () => string;
}

interface BlocksCustomData {
  blocks: BlockModel[];
  dragStart: (block: BlockModel, ev?: Event) => void;
  drag: (ev: Event) => void;
  dragStop: (cancel?: boolean) => void;
}

/**
 * The block manager's current custom payload.
 *
 * `block:custom` only fires while blocks are being registered, which is over
 * before this panel exists — so the payload is read directly instead of waited
 * for. `__customData` is GrapesJS-internal (hence the guard): if a future
 * version drops it the palette degrades to waiting for the next event rather
 * than throwing.
 */
function currentBlocks(editor: Editor): BlocksCustomData | null {
  const manager = editor.BlockManager as unknown as { __customData?: () => BlocksCustomData };
  return typeof manager.__customData === 'function' ? manager.__customData() : null;
}

export function BlocksPanel({ editor }: { editor: Editor | null }) {
  const { t } = useTranslation();
  const payload = useCustomPayload<BlocksCustomData>(editor, 'block:custom', currentBlocks);
  const [query, setQuery] = useState('');
  const [dragging, setDragging] = useState<string | null>(null);

  const groups = useMemo(() => {
    const blocks = payload?.blocks ?? [];
    const needle = query.trim().toLowerCase();
    const matched = needle
      ? blocks.filter(
          (b) =>
            b.getLabel().toLowerCase().includes(needle) ||
            b.getCategoryLabel().toLowerCase().includes(needle),
        )
      : blocks;

    const byCategory = new Map<string, BlockModel[]>();
    for (const block of matched) {
      const label = block.getCategoryLabel() || t('builder.blocks');
      const list = byCategory.get(label) ?? [];
      list.push(block);
      byCategory.set(label, list);
    }
    return [...byCategory.entries()];
  }, [payload, query, t]);

  if (!payload) {
    return <div className="p-4 text-sm text-muted-foreground">{t('common.loading')}</div>;
  }

  return (
    <div className="flex h-full flex-col">
      <div className="relative border-b p-2">
        <Search className="pointer-events-none absolute left-4 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
        <Input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder={t('builder.searchBlocks')}
          className="pl-8"
        />
      </div>

      <div className="min-h-0 flex-1 overflow-auto p-2">
        {groups.length === 0 && (
          <p className="p-2 text-sm text-muted-foreground">{t('common.noResults')}</p>
        )}
        {groups.map(([category, blocks]) => (
          <section key={category} className="mb-4">
            <h3 className="px-1 pb-1.5 text-xs font-medium uppercase tracking-wide text-muted-foreground">
              {category}
            </h3>
            <div className="grid grid-cols-2 gap-1.5">
              {blocks.map((block) => {
                const id = block.getId();
                return (
                  <button
                    key={id}
                    type="button"
                    draggable
                    title={String(block.get('attributes') && (block.get('attributes') as { title?: string }).title) || block.getLabel()}
                    onDragStart={(e) => {
                      setDragging(id);
                      // GrapesJS reads the native event to position the drop
                      // indicator inside the canvas iframe.
                      payload.dragStart(block, e.nativeEvent);
                    }}
                    onDrag={(e) => payload.drag(e.nativeEvent)}
                    onDragEnd={() => {
                      setDragging(null);
                      payload.dragStop(false);
                    }}
                    className={cn(
                      'flex cursor-grab flex-col items-center gap-1.5 rounded-md border bg-background p-2 text-center text-[11px] leading-tight transition-colors',
                      'hover:border-primary/50 hover:bg-accent',
                      dragging === id && 'opacity-50',
                    )}
                  >
                    <span
                      className="flex h-6 w-6 items-center justify-center text-muted-foreground"
                      // Block media is an inline SVG string owned by the block
                      // library, not user input.
                      dangerouslySetInnerHTML={{ __html: block.getMedia() ?? '' }}
                    />
                    <span className="line-clamp-2">{block.getLabel()}</span>
                  </button>
                );
              })}
            </div>
          </section>
        ))}
      </div>
    </div>
  );
}
