import { availableComponents } from '@dcms/site-components';
import { FileText, Layers as LayersIcon, Plus, Shapes, Trash2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/button';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../../components/ui/tabs';
import { cn } from '../../lib/cn';
import { useEditor } from './store';

export function LeftPanel({ enabledPluginIds }: { enabledPluginIds: Set<string> }) {
  const { t } = useTranslation();
  return (
    <aside className="flex w-64 shrink-0 flex-col border-r bg-card">
      <Tabs defaultValue="components" className="flex flex-1 flex-col">
        <TabsList className="m-2">
          <TabsTrigger value="components" className="flex-1">
            <Shapes className="h-4 w-4" />
          </TabsTrigger>
          <TabsTrigger value="pages" className="flex-1">
            <FileText className="h-4 w-4" />
          </TabsTrigger>
          <TabsTrigger value="layers" className="flex-1">
            <LayersIcon className="h-4 w-4" />
          </TabsTrigger>
        </TabsList>
        <div className="flex-1 overflow-y-auto px-2 pb-2">
          <TabsContent value="components" className="mt-0">
            <Palette enabledPluginIds={enabledPluginIds} />
          </TabsContent>
          <TabsContent value="pages" className="mt-0">
            <Pages />
          </TabsContent>
          <TabsContent value="layers" className="mt-0">
            <LayersList />
          </TabsContent>
        </div>
      </Tabs>
      <span className="sr-only">{t('editor.layers')}</span>
    </aside>
  );
}

function Palette({ enabledPluginIds }: { enabledPluginIds: Set<string> }) {
  const addNode = useEditor((s) => s.addNode);
  const items = availableComponents(enabledPluginIds);
  return (
    <div className="grid grid-cols-2 gap-2">
      {items.map((c) => (
        <button
          key={c.type}
          type="button"
          onClick={() => addNode(c.type)}
          className="flex flex-col items-start gap-1 rounded-md border bg-background p-2.5 text-left text-xs transition-colors hover:border-primary hover:bg-accent/40"
        >
          <span className="font-medium">{c.displayName}</span>
          <span className="text-[10px] uppercase text-muted-foreground">{c.category}</span>
        </button>
      ))}
    </div>
  );
}

function Pages() {
  const { t } = useTranslation();
  const def = useEditor((s) => s.history.present);
  const pageId = useEditor((s) => s.pageId);
  const selectPage = useEditor((s) => s.selectPage);
  const addPage = useEditor((s) => s.addPage);
  return (
    <div className="space-y-1">
      {def.pages.map((p) => (
        <button
          key={p.id}
          type="button"
          onClick={() => selectPage(p.id)}
          className={cn(
            'flex w-full items-center justify-between rounded-md px-2.5 py-2 text-sm',
            p.id === pageId ? 'bg-accent text-accent-foreground' : 'hover:bg-accent/50',
          )}
        >
          <span className="truncate">{p.title || p.path}</span>
          <span className="text-[10px] text-muted-foreground">{p.path}</span>
        </button>
      ))}
      <Button variant="outline" size="sm" className="mt-2 w-full" onClick={addPage}>
        <Plus className="h-4 w-4" /> {t('editor.addPage')}
      </Button>
    </div>
  );
}

function LayersList() {
  const { t } = useTranslation();
  const nodes = useEditor((s) => s.currentNodes());
  const selectedId = useEditor((s) => s.selectedId);
  const select = useEditor((s) => s.select);
  const removeSelected = useEditor((s) => s.removeSelected);

  const ordered = nodes.slice().sort((a, b) => (b.layout?.z ?? 0) - (a.layout?.z ?? 0));
  if (ordered.length === 0) {
    return <p className="px-2 py-4 text-xs text-muted-foreground">{t('common.noResults')}</p>;
  }
  return (
    <div className="space-y-1">
      {ordered.map((n) => (
        <div
          key={n.id}
          className={cn(
            'flex items-center justify-between rounded-md px-2.5 py-1.5 text-sm',
            n.id === selectedId ? 'bg-accent text-accent-foreground' : 'hover:bg-accent/50',
          )}
        >
          <button type="button" className="flex-1 truncate text-left" onClick={() => select(n.id)}>
            {n.type}
          </button>
          {n.id === selectedId ? (
            <button type="button" onClick={removeSelected} className="text-destructive">
              <Trash2 className="h-3.5 w-3.5" />
            </button>
          ) : null}
        </div>
      ))}
    </div>
  );
}
