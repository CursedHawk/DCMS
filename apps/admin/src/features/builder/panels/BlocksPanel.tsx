import { OWN_COMPONENTS_GROUP } from '@dcms/gjs-schema';
import type { Editor } from 'grapesjs';
import { ChevronDown, ChevronRight, ChevronsDownUp, ChevronsUpDown, LayoutGrid, List, Search } from 'lucide-react';
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
 *
 * Tiles show the wireframe the block library draws for each spec, not a 22px
 * glyph. With ninety-odd blocks, three of which have four-rounded-rectangle
 * icons, the glyph told an author only that a block existed; the wireframe tells
 * them what dropping it will produce. The compact density keeps the glyph, for
 * when the panel is narrow or the author already knows what they are looking for.
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

type Density = 'gallery' | 'compact';

const DENSITY_KEY = 'dcms.builder.blockDensity';
/** Which category sections the author has collapsed, remembered across sessions. */
const SECTIONS_KEY = 'dcms.builder.blockSections';
/**
 * How many sections start open.
 *
 * The palette carries ninety-odd blocks across a dozen categories; open, that is
 * a scroll the length of the panel several times over, and finding "Testimonials"
 * meant knowing it was two thirds of the way down. Everything collapsed is no
 * better — a palette that shows nothing does not read as a palette — so the first
 * few stay open and the rest are one click away.
 */
const OPEN_BY_DEFAULT = 2;
/** Tenant-built components land in one group (see @dcms/gjs-schema); it is always worth showing. */

function storedSections(): Record<string, boolean> {
  try {
    const raw = localStorage.getItem(SECTIONS_KEY);
    const parsed = raw ? (JSON.parse(raw) as unknown) : null;
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, boolean>) : {};
  } catch {
    // A corrupt entry must not cost the author their palette.
    return {};
  }
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

function titleOf(block: BlockModel): string {
  const docs = block.get('docs');
  if (typeof docs === 'string' && docs) return docs;
  const attributes = block.get('attributes') as { title?: string } | undefined;
  return attributes?.title ?? block.getLabel();
}

function thumbnailOf(block: BlockModel): string {
  const thumb = block.get('thumbnail');
  return typeof thumb === 'string' && thumb ? thumb : (block.getMedia() ?? '');
}

export function BlocksPanel({ editor }: { editor: Editor | null }) {
  const { t } = useTranslation();
  const payload = useCustomPayload<BlocksCustomData>(editor, 'block:custom', currentBlocks);
  const [query, setQuery] = useState('');
  const [dragging, setDragging] = useState<string | null>(null);
  const [density, setDensity] = useState<Density>(
    () => (localStorage.getItem(DENSITY_KEY) as Density | null) ?? 'gallery',
  );
  const [sections, setSections] = useState<Record<string, boolean>>(storedSections);

  const chooseDensity = (next: Density) => {
    setDensity(next);
    localStorage.setItem(DENSITY_KEY, next);
  };

  const remember = (next: Record<string, boolean>) => {
    setSections(next);
    try {
      localStorage.setItem(SECTIONS_KEY, JSON.stringify(next));
    } catch {
      /* private mode; the palette still works, it just forgets */
    }
  };

  const toggleSection = (category: string, open: boolean) =>
    remember({ ...sections, [category]: open });

  const groups = useMemo(() => {
    const blocks = payload?.blocks ?? [];
    const needle = query.trim().toLowerCase();
    // Searching the description too, because an author looking for "alternating
    // rows" should find the block whose label is "Feature list".
    const matched = needle
      ? blocks.filter(
          (b) =>
            b.getLabel().toLowerCase().includes(needle) ||
            b.getCategoryLabel().toLowerCase().includes(needle) ||
            titleOf(b).toLowerCase().includes(needle),
        )
      : blocks;

    const byCategory = new Map<string, BlockModel[]>();
    for (const block of matched) {
      const label = block.getCategoryLabel() || t('builder.blocks');
      const list = byCategory.get(label) ?? [];
      list.push(block);
      byCategory.set(label, list);
    }

    // The tenant's own components first. They are the ones this site actually
    // uses, and they were arriving last simply because they register last.
    return [...byCategory.entries()].sort(
      ([a], [b]) => Number(b === OWN_COMPONENTS_GROUP) - Number(a === OWN_COMPONENTS_GROUP),
    );
  }, [payload, query, t]);

  // A search that matched three blocks in a collapsed section would look like a
  // search that matched nothing, so searching overrides the stored state.
  const searching = query.trim().length > 0;
  const isOpen = (category: string, index: number) =>
    searching || (sections[category] ?? (index < OPEN_BY_DEFAULT || category === OWN_COMPONENTS_GROUP));
  const allOpen = groups.every(([category], index) => isOpen(category, index));

  if (!payload) {
    return <div className="p-4 text-sm text-muted-foreground">{t('common.loading')}</div>;
  }

  const dragProps = (block: BlockModel, id: string) => ({
    draggable: true,
    onDragStart: (e: React.DragEvent) => {
      setDragging(id);
      // GrapesJS reads the native event to position the drop indicator inside
      // the canvas iframe.
      payload.dragStart(block, e.nativeEvent);
    },
    onDrag: (e: React.DragEvent) => payload.drag(e.nativeEvent),
    onDragEnd: () => {
      setDragging(null);
      payload.dragStop(false);
    },
  });

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center gap-1 border-b p-2">
        <div className="relative flex-1">
          <Search className="pointer-events-none absolute left-2 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('builder.searchBlocks')}
            className="pl-8"
          />
        </div>
        <button
          type="button"
          title={allOpen ? t('builder.collapseAll') : t('builder.expandAll')}
          aria-label={allOpen ? t('builder.collapseAll') : t('builder.expandAll')}
          disabled={searching}
          onClick={() =>
            remember(Object.fromEntries(groups.map(([category]) => [category, !allOpen])))
          }
          className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md border text-muted-foreground hover:text-foreground disabled:opacity-40"
        >
          {allOpen ? <ChevronsDownUp className="h-4 w-4" /> : <ChevronsUpDown className="h-4 w-4" />}
        </button>
        <div className="flex shrink-0 rounded-md border">
          {(
            [
              ['gallery', LayoutGrid, t('builder.blockDensityGallery')],
              ['compact', List, t('builder.blockDensityCompact')],
            ] as const
          ).map(([value, Icon, label]) => (
            <button
              key={value}
              type="button"
              title={label}
              aria-label={label}
              aria-pressed={density === value}
              onClick={() => chooseDensity(value)}
              className={cn(
                'flex h-8 w-8 items-center justify-center text-muted-foreground transition-colors first:rounded-l-md last:rounded-r-md',
                density === value && 'bg-accent text-foreground',
              )}
            >
              <Icon className="h-4 w-4" />
            </button>
          ))}
        </div>
      </div>

      <div className="min-h-0 flex-1 overflow-auto p-2">
        {groups.length === 0 && (
          <p className="p-2 text-sm text-muted-foreground">{t('common.noResults')}</p>
        )}
        {groups.map(([category, blocks], index) => {
          const open = isOpen(category, index);
          return (
          <section key={category} className="mb-3">
            <button
              type="button"
              aria-expanded={open}
              onClick={() => toggleSection(category, !open)}
              className="flex w-full items-center gap-1 rounded px-1 py-1 text-left text-xs font-medium uppercase tracking-wide text-muted-foreground hover:bg-accent/50 hover:text-foreground"
            >
              {open ? (
                <ChevronDown className="h-3.5 w-3.5 shrink-0" />
              ) : (
                <ChevronRight className="h-3.5 w-3.5 shrink-0" />
              )}
              <span className="min-w-0 flex-1 truncate">{category}</span>
              {/* The count is what makes a collapsed section worth collapsing:
                  it says whether opening it is worth the scroll. */}
              <span className="shrink-0 text-[10px] tabular-nums opacity-70">{blocks.length}</span>
            </button>

            {!open ? null : density === 'gallery' ? (
              <div className="grid grid-cols-2 gap-2">
                {blocks.map((block) => {
                  const id = block.getId();
                  return (
                    <button
                      key={id}
                      type="button"
                      title={titleOf(block)}
                      {...dragProps(block, id)}
                      className={cn(
                        'group flex cursor-grab flex-col overflow-hidden rounded-md border bg-background text-left transition-colors',
                        'hover:border-primary/60 hover:bg-accent/40',
                        dragging === id && 'opacity-50',
                      )}
                    >
                      <span
                        className={cn(
                          'block aspect-[8/5] w-full bg-muted/40 text-foreground/80',
                          // The wireframe paints its own accents through this class.
                          '[&_.dcms-thumb-accent]:fill-primary [&_.dcms-thumb-accent]:opacity-90',
                          '[&_svg]:block [&_svg]:h-full [&_svg]:w-full',
                        )}
                        // Block media and thumbnails are inline SVG strings owned
                        // by the block library, not user input.
                        dangerouslySetInnerHTML={{ __html: thumbnailOf(block) }}
                      />
                      <span className="line-clamp-2 px-1.5 py-1 text-[11px] leading-tight">
                        {block.getLabel()}
                      </span>
                    </button>
                  );
                })}
              </div>
            ) : (
              <div className="flex flex-col gap-0.5">
                {blocks.map((block) => {
                  const id = block.getId();
                  return (
                    <button
                      key={id}
                      type="button"
                      title={titleOf(block)}
                      {...dragProps(block, id)}
                      className={cn(
                        'flex cursor-grab items-center gap-2 rounded-md px-1.5 py-1 text-left text-xs transition-colors',
                        'hover:bg-accent',
                        dragging === id && 'opacity-50',
                      )}
                    >
                      <span
                        className="flex h-5 w-5 shrink-0 items-center justify-center text-muted-foreground [&_svg]:h-4 [&_svg]:w-4"
                        dangerouslySetInnerHTML={{ __html: block.getMedia() ?? '' }}
                      />
                      <span className="truncate">{block.getLabel()}</span>
                    </button>
                  );
                })}
              </div>
            )}
          </section>
          );
        })}
      </div>
    </div>
  );
}
