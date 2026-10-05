import { CODE_PREFIX, type ComponentDefinition } from '@dcms/site-runtime';
import type { Editor } from 'grapesjs';
import { Search } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Input, cn } from '@dcms/ui';
import { useCustomPayload } from '../../builder/panels/useEditorEvent';
import { thumbnailOf } from '../catalog/look';
import { insertComponent } from '../canvas/insert';
import { useVisual } from '../store';
import { LivePreview } from './LivePreview';

interface BlockModel {
  getId: () => string | number;
}

interface BlocksCustomData {
  blocks: BlockModel[];
  dragStart: (block: BlockModel, ev?: Event) => void;
  drag: (ev: Event) => void;
  dragStop: (cancel?: boolean) => void;
}

/** See BlocksPanel: the block manager's payload is read directly, its event has already fired. */
function currentBlocks(editor: Editor): BlocksCustomData | null {
  const manager = editor.BlockManager as unknown as { __customData?: () => BlocksCustomData };
  return typeof manager.__customData === 'function' ? manager.__customData() : null;
}

type Tab = 'elements' | 'mine' | 'developer';
const TAB_KEY = 'dcms.visual.paletteTab';
/** The order categories appear in; anything else follows alphabetically. */
const CATEGORY_ORDER = ['Layout', 'Content', 'Media', 'Data', 'Forms', 'Navigation'];

function storedTab(): Tab {
  try {
    const value = localStorage.getItem(TAB_KEY);
    return value === 'mine' || value === 'developer' ? value : 'elements';
  } catch {
    return 'elements';
  }
}

function matches(def: ComponentDefinition, query: string): boolean {
  if (!query) return true;
  const haystack = [def.label, def.description ?? '', def.category, ...(def.keywords ?? [])].join(' ').toLowerCase();
  return query
    .toLowerCase()
    .split(/\s+/)
    .every((word) => haystack.includes(word));
}

/**
 * The Mode D palette: what can be added, as pictures. Elements are the built-ins, Mine the
 * site's own components (drawn live, in this site's theme), Developer the code components.
 * Search reads labels, descriptions and the words people look for things by; a card is dragged
 * onto the canvas or clicked to add it after the selection.
 */
export function VisualPalette({ editor }: { editor: Editor | null }) {
  const { t } = useTranslation();
  const payload = useCustomPayload<BlocksCustomData>(editor, 'block:custom', currentBlocks);
  const registry = useVisual((s) => s.registry);
  const blocked = useVisual((s) => s.blockedTypes);
  const app = useVisual((s) => s.app);
  const [tab, setTab] = useState<Tab>(storedTab);
  const [query, setQuery] = useState('');

  const placeable = useMemo(
    () => [...registry.values()].filter((d) => d.draggable !== false && d.allowedParents?.length !== 0 && !blocked.has(d.type)),
    [registry, blocked],
  );
  const kindOf = (d: ComponentDefinition): Tab => (d.template ? 'mine' : d.type.startsWith(CODE_PREFIX) ? 'developer' : 'elements');
  const counts = { elements: 0, mine: 0, developer: 0 };
  for (const d of placeable) counts[kindOf(d)]++;
  const shown = placeable.filter((d) => kindOf(d) === tab && matches(d, query.trim()));
  const groups = [...new Set(shown.map((d) => d.category))]
    .sort((a, b) => {
      const ia = CATEGORY_ORDER.indexOf(a);
      const ib = CATEGORY_ORDER.indexOf(b);
      return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a.localeCompare(b);
    })
    .map((category) => ({ category, defs: shown.filter((d) => d.category === category) }));

  const choose = (next: Tab) => {
    setTab(next);
    try {
      localStorage.setItem(TAB_KEY, next);
    } catch {
      // Remembering the tab is a convenience.
    }
  };

  const add = (def: ComponentDefinition) => {
    if (!editor) return;
    const out = insertComponent(editor, registry, def.type);
    if (!out.ok) toast.error(out.reason);
  };

  const block = (type: string) => payload?.blocks.find((b) => String(b.getId()) === `dcms-d:${type}`);

  const tabs: Tab[] = ['elements', 'mine', 'developer'];
  return (
    <div className="flex h-full flex-col">
      <div className="space-y-2 border-b p-2">
        <div className="relative">
          <Search className="pointer-events-none absolute left-2 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('visual.palette.search')}
            aria-label={t('visual.palette.search')}
            className="pl-8"
          />
        </div>
        <div role="tablist" aria-label={t('visual.palette.kinds')} className="flex rounded-md border bg-muted/40 p-0.5">
          {tabs
            .filter((k) => k === 'elements' || counts[k] > 0)
            .map((k) => (
              <button
                key={k}
                type="button"
                role="tab"
                aria-selected={tab === k}
                onClick={() => choose(k)}
                className={cn(
                  'flex-1 rounded px-2 py-1 text-xs',
                  tab === k ? 'bg-background font-medium shadow-sm' : 'text-muted-foreground hover:text-foreground',
                )}
              >
                {t(`visual.palette.${k}`)}
                {k !== 'elements' && <span className="ml-1 tabular-nums opacity-60">{counts[k]}</span>}
              </button>
            ))}
        </div>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto p-2">
        {groups.length === 0 && (
          <p className="p-3 text-sm text-muted-foreground">{query.trim() ? t('visual.palette.noMatch', { query: query.trim() }) : t(`visual.palette.none.${tab}`)}</p>
        )}
        {groups.map(({ category, defs }) => (
          <section key={category} className="mb-3">
            <h3 className="px-1 pb-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">{category}</h3>
            <div className="grid grid-cols-2 gap-2">
              {defs.map((def) => {
                const b = block(def.type);
                return (
                  <button
                    key={def.type}
                    type="button"
                    title={def.description}
                    aria-description={def.description}
                    draggable={!!b && !!payload}
                    onDragStart={(e) => b && payload?.dragStart(b, e.nativeEvent)}
                    onDrag={(e) => payload?.drag(e.nativeEvent)}
                    onDragEnd={() => payload?.dragStop(false)}
                    onClick={() => add(def)}
                    className="group flex cursor-grab flex-col overflow-hidden rounded-md border bg-background text-left transition-colors hover:border-primary/60 hover:bg-accent/40"
                  >
                    {def.template ? (
                      <span className="relative block aspect-[8/5] w-full overflow-hidden bg-muted/40">
                        <LivePreview node={{ id: 'preview', type: def.type, version: def.version }} registry={registry} app={app} />
                      </span>
                    ) : (
                      <span
                        className={cn(
                          'block aspect-[8/5] w-full bg-muted/40 text-foreground/80',
                          '[&_.dcms-thumb-accent]:fill-primary [&_.dcms-thumb-accent]:opacity-90',
                          '[&_svg]:block [&_svg]:h-full [&_svg]:w-full',
                        )}
                        // Wireframes are SVG markup owned by the block library, not user input.
                        dangerouslySetInnerHTML={{ __html: thumbnailOf(def) }}
                      />
                    )}
                    <span className="line-clamp-2 px-1.5 py-1 text-[11px] leading-tight">{def.label}</span>
                  </button>
                );
              })}
            </div>
          </section>
        ))}
        <p className="px-1 pt-1 text-[11px] text-muted-foreground">{t('visual.palette.hint')}</p>
      </div>
    </div>
  );
}
