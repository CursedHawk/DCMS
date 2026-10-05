import { CODE_PREFIX, type ComponentDefinition } from '@dcms/site-runtime';
import type { Editor } from 'grapesjs';
import { Search } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Input, cn } from '@dcms/ui';
import { HelpLink } from '../help/HelpLink';
import { useCustomPayload } from '../../builder/panels/useEditorEvent';
import { thumbnailOf } from '../catalog/look';
import { insertComponent } from '../canvas/insert';
import { nodeFromStarter, toGrapes } from '../canvas/tree';
import { ADD_SECTION } from '../canvas/types';
import { SECTION_TEMPLATES, type SectionTemplate } from '../templates/sections';
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

type Tab = 'elements' | 'sections' | 'mine' | 'developer';
const TAB_KEY = 'dcms.visual.paletteTab';
/** The order categories appear in; anything else follows alphabetically. */
const CATEGORY_ORDER = ['Layout', 'Content', 'Media', 'Data', 'Forms', 'Navigation'];

function storedTab(): Tab {
  try {
    const value = localStorage.getItem(TAB_KEY);
    return value === 'sections' || value === 'mine' || value === 'developer' ? value : 'elements';
  } catch {
    return 'elements';
  }
}

function matches(def: Pick<ComponentDefinition, 'label' | 'description' | 'category' | 'keywords'>, query: string): boolean {
  if (!query) return true;
  const haystack = [def.label, def.description ?? '', def.category, ...(def.keywords ?? [])].join(' ').toLowerCase();
  return query
    .toLowerCase()
    .split(/\s+/)
    .every((word) => haystack.includes(word));
}

/**
 * The Mode D palette: what can be added, as pictures. Elements are the built-ins, Sections
 * finished bands of a page built from them (drawn live, in this site's theme), Mine the site's
 * own components (drawn live, in this site's theme), Developer the code components.
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
  // Section previews are built once per registry: drawing them is the expensive part.
  const sections = useMemo(() => SECTION_TEMPLATES.map((template) => ({ template, node: nodeFromStarter(template.tree, registry) })), [registry]);
  const kindOf = (d: ComponentDefinition): Tab => (d.template ? 'mine' : d.type.startsWith(CODE_PREFIX) ? 'developer' : 'elements');
  const counts = { elements: 0, sections: SECTION_TEMPLATES.length, mine: 0, developer: 0 };
  for (const d of placeable) counts[kindOf(d)]++;
  const shown = placeable.filter((d) => kindOf(d) === tab && matches(d, query.trim()));
  const shownSections = tab === 'sections' ? sections.filter(({ template }) => matches(template, query.trim())) : [];
  const sectionGroups = [...new Set(shownSections.map((s) => s.template.category))].map((category) => ({
    category,
    items: shownSections.filter((s) => s.template.category === category),
  }));
  const groups = [...new Set(shown.map((d) => d.category))]
    .sort((a, b) => {
      const ia = CATEGORY_ORDER.indexOf(a);
      const ib = CATEGORY_ORDER.indexOf(b);
      return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a.localeCompare(b);
    })
    .map((category) => ({ category, defs: shown.filter((d) => d.category === category) }));

  // The canvas toolbar's "add a section below" opens this tab.
  useEffect(() => {
    if (!editor) return;
    const open = () => choose('sections');
    editor.on(ADD_SECTION, open);
    return () => {
      editor.off(ADD_SECTION, open);
    };
  }, [editor]);

  function choose(next: Tab) {
    setTab(next);
    try {
      localStorage.setItem(TAB_KEY, next);
    } catch {
      // Remembering the tab is a convenience.
    }
  }

  const add = (def: ComponentDefinition) => {
    if (!editor) return;
    const out = insertComponent(editor, registry, def.type);
    if (!out.ok) toast.error(out.reason);
  };

  const addSection = (template: SectionTemplate) => {
    if (!editor) return;
    // Fresh ids for every insert: the same template may go on a page twice.
    const tree = toGrapes(nodeFromStarter(template.tree, registry), registry);
    const out = insertComponent(editor, registry, template.tree.type, tree, { band: true });
    if (!out.ok) toast.error(out.reason);
  };

  const block = (type: string) => payload?.blocks.find((b) => String(b.getId()) === `dcms-d:${type}`);

  const tabs: Tab[] = ['elements', 'sections', 'mine', 'developer'];
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
            .filter((k) => k === 'elements' || k === 'sections' || counts[k] > 0)
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
                {(k === 'mine' || k === 'developer') && <span className="ml-1 tabular-nums opacity-60">{counts[k]}</span>}
              </button>
            ))}
        </div>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto p-2">
        {sectionGroups.map(({ category, items }) => (
          <section key={category} className="mb-3">
            <h3 className="px-1 pb-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">{category}</h3>
            <div className="space-y-2">
              {items.map(({ template, node }) => (
                <button
                  key={template.id}
                  type="button"
                  title={template.description}
                  onClick={() => addSection(template)}
                  className="group flex w-full flex-col overflow-hidden rounded-md border bg-background text-left transition-colors hover:border-primary/60 hover:bg-accent/40"
                >
                  <span className="relative block aspect-[16/9] w-full overflow-hidden bg-muted/40">
                    <LivePreview node={node} registry={registry} app={app} mode="edit" />
                  </span>
                  <span className="px-1.5 py-1 text-[11px] leading-tight">
                    <span className="font-medium">{template.label}</span>
                    <span className="block text-muted-foreground">{template.description}</span>
                  </span>
                </button>
              ))}
            </div>
          </section>
        ))}
        {tab !== 'sections' && groups.length === 0 && (
          <p className="p-3 text-sm text-muted-foreground">{query.trim() ? t('visual.palette.noMatch', { query: query.trim() }) : t(`visual.palette.none.${tab}`)}</p>
        )}
        {tab === 'sections' && sectionGroups.length === 0 && <p className="p-3 text-sm text-muted-foreground">{t('visual.palette.noMatch', { query: query.trim() })}</p>}
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
        <p className="px-1 pt-1 text-[11px] text-muted-foreground">
          {t(tab === 'sections' ? 'visual.palette.sectionsHint' : 'visual.palette.hint')} <HelpLink article="sections" className="align-middle" />
        </p>
      </div>
    </div>
  );
}
