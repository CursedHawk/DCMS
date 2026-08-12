import { FileText, LayoutGrid, Plus, Search } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Page, PageHeader } from '../../components/Page';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { EmptyState } from '../../components/ui/empty-state';
import { Input } from '../../components/ui/input';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '../../components/ui/select';
import { CenteredSpinner } from '../../components/ui/spinner';
import { TBody, TD, TH, THead, TR, Table } from '../../components/ui/table';
import { cn } from '../../lib/cn';
import {
  type ContentTypeDef,
  type PluginInstance,
  usePluginCatalog,
  usePluginInstances,
} from '../plugins/api';
import { ContentEditor } from './ContentEditor';
import { type ContentItem, useContentItems, useContentItemsOfType } from './api';

/** Turn a machine name ("blogPost", "gig_line-up") into a readable label. */
function humanize(name: string): string {
  const spaced = name
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .trim();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

/** Field whose value best represents an item in a list (first textual field). */
function titleFieldOf(ct: ContentTypeDef): string | undefined {
  const textual = ct.fields.find((f) => f.type === 'Text' || f.type === 'RichText' || f.type === 'Markdown');
  return textual?.name ?? ct.slugField;
}

function itemTitle(item: ContentItem, titleField: string | undefined): string {
  const raw = titleField ? item.draft?.[titleField] : undefined;
  const text = typeof raw === 'string' ? raw.trim() : '';
  return text || item.slug;
}

function statusTone(status: string): 'success' | 'warning' | 'secondary' {
  if (status === 'Published') return 'success';
  if (status === 'Scheduled') return 'warning';
  return 'secondary';
}

export function ContentPage() {
  const { t } = useTranslation();
  const instances = usePluginInstances();
  const catalog = usePluginCatalog();
  const [selected, setSelected] = useState<{ instanceId: string; type: string } | null>(null);

  const manifestFor = (pluginId: string) => catalog.data?.find((m) => m.id === pluginId);

  // Only enabled instances that actually author content belong in the navigator.
  const authorable = useMemo(
    () =>
      (instances.data ?? [])
        .filter((i) => i.enabled && (manifestFor(i.pluginId)?.contentTypes.length ?? 0) > 0)
        .map((instance) => ({
          instance,
          types: manifestFor(instance.pluginId)?.contentTypes ?? [],
        })),
    [instances.data, catalog.data],
  );

  // Default to the first collection once data lands.
  useEffect(() => {
    if (selected || authorable.length === 0) return;
    const first = authorable.find((g) => g.types.length > 0);
    if (first) setSelected({ instanceId: first.instance.id, type: first.types[0].name });
  }, [authorable, selected]);

  const activeGroup = authorable.find((g) => g.instance.id === selected?.instanceId);
  const activeType = activeGroup?.types.find((ct) => ct.name === selected?.type);

  if (instances.isLoading || catalog.isLoading) {
    return (
      <Page className="max-w-7xl">
        <PageHeader title={t('content.title')} description={t('content.subtitle')} />
        <CenteredSpinner />
      </Page>
    );
  }

  return (
    <Page className="max-w-7xl">
      <PageHeader title={t('content.title')} description={t('content.subtitle')} />

      {authorable.length === 0 ? (
        <EmptyState
          icon={LayoutGrid}
          title={t('content.noCollections')}
          description={t('content.noCollectionsHint')}
        />
      ) : (
        <div className="grid gap-6 lg:grid-cols-[260px_minmax(0,1fr)]">
          <aside className="space-y-4">
            <p className="px-1 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
              {t('content.collections')}
            </p>
            <nav className="space-y-4">
              {authorable.map((group) => (
                <InstanceNav
                  key={group.instance.id}
                  instance={group.instance}
                  types={group.types}
                  selectedType={selected?.instanceId === group.instance.id ? selected.type : null}
                  onSelect={(type) => setSelected({ instanceId: group.instance.id, type })}
                />
              ))}
            </nav>
          </aside>

          <section className="min-w-0">
            {activeGroup && activeType ? (
              <CollectionView
                key={`${activeGroup.instance.id}:${activeType.name}`}
                instance={activeGroup.instance}
                contentType={activeType}
              />
            ) : (
              <EmptyState icon={FileText} title={t('content.selectCollection')} />
            )}
          </section>
        </div>
      )}
    </Page>
  );
}

/** One instance in the left rail: its name plus a button per content type with a count. */
function InstanceNav({
  instance,
  types,
  selectedType,
  onSelect,
}: {
  instance: PluginInstance;
  types: ContentTypeDef[];
  selectedType: string | null;
  onSelect: (type: string) => void;
}) {
  const items = useContentItems(instance.id);
  const countOf = (type: string) => (items.data ?? []).filter((i) => i.contentType === type).length;

  return (
    <div>
      <p className="truncate px-2 pb-1 text-xs font-medium text-muted-foreground">{instance.name}</p>
      <div className="space-y-0.5">
        {types.map((ct) => {
          const active = selectedType === ct.name;
          return (
            <button
              key={ct.name}
              type="button"
              onClick={() => onSelect(ct.name)}
              className={cn(
                'flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm transition-colors',
                active
                  ? 'bg-accent font-medium text-accent-foreground'
                  : 'text-foreground hover:bg-muted/60',
              )}
            >
              <FileText className={cn('h-4 w-4 shrink-0', active ? '' : 'text-muted-foreground')} />
              <span className="flex-1 truncate">{humanize(ct.name)}</span>
              {items.isSuccess ? (
                <span
                  className={cn(
                    'shrink-0 text-xs tabular-nums',
                    active ? 'text-accent-foreground/70' : 'text-muted-foreground',
                  )}
                >
                  {countOf(ct.name)}
                </span>
              ) : null}
            </button>
          );
        })}
      </div>
    </div>
  );
}

/** Right pane: searchable, filterable list of one collection's items. */
function CollectionView({
  instance,
  contentType,
}: {
  instance: PluginInstance;
  contentType: ContentTypeDef;
}) {
  const { t } = useTranslation();
  // includeDraft so the list can show a real title, not just the slug.
  const items = useContentItemsOfType(instance.id, contentType.name);
  const [editorItem, setEditorItem] = useState<string | null | undefined>(undefined);
  const [query, setQuery] = useState('');
  const [status, setStatus] = useState('all');

  const titleField = titleFieldOf(contentType);

  const all = items.data ?? [];
  const filtered = all.filter((item) => {
    if (status !== 'all' && item.status !== status) return false;
    if (query.trim()) {
      const q = query.trim().toLowerCase();
      return (
        item.slug.toLowerCase().includes(q) ||
        itemTitle(item, titleField).toLowerCase().includes(q)
      );
    }
    return true;
  });

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0">
          <h2 className="truncate text-lg font-semibold tracking-tight">{humanize(contentType.name)}</h2>
          <p className="truncate text-xs text-muted-foreground">
            {instance.name} · {t('content.itemCount', { count: all.length })}
          </p>
        </div>
        <Button size="sm" onClick={() => setEditorItem(null)}>
          <Plus className="h-4 w-4" /> {t('content.newItem')}
        </Button>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <div className="relative min-w-0 flex-1">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('content.searchPlaceholder')}
            className="pl-8"
          />
        </div>
        <Select value={status} onValueChange={setStatus}>
          <SelectTrigger className="w-40">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">{t('content.allStatuses')}</SelectItem>
            <SelectItem value="Published">{t('content.published')}</SelectItem>
            <SelectItem value="Draft">{t('content.draft')}</SelectItem>
            <SelectItem value="Scheduled">{t('content.scheduled')}</SelectItem>
          </SelectContent>
        </Select>
      </div>

      {items.isLoading ? (
        <CenteredSpinner />
      ) : filtered.length > 0 ? (
        <Table>
          <THead>
            <TR>
              <TH>{t('content.itemTitle')}</TH>
              <TH>{t('common.slug')}</TH>
              <TH>{t('common.status')}</TH>
              <TH>{t('common.updated')}</TH>
            </TR>
          </THead>
          <TBody>
            {filtered.map((item) => (
              <TR key={item.id} className="cursor-pointer" onClick={() => setEditorItem(item.id)}>
                <TD className="font-medium">{itemTitle(item, titleField)}</TD>
                <TD className="text-muted-foreground">{item.slug}</TD>
                <TD>
                  <Badge tone={statusTone(item.status)}>{item.status}</Badge>
                </TD>
                <TD className="text-muted-foreground">{new Date(item.updatedAt).toLocaleString()}</TD>
              </TR>
            ))}
          </TBody>
        </Table>
      ) : all.length > 0 ? (
        <EmptyState icon={Search} title={t('common.noResults')} />
      ) : (
        <EmptyState
          icon={FileText}
          title={t('content.emptyCollection', { type: humanize(contentType.name) })}
          description={t('content.emptyCollectionHint')}
          action={
            <Button size="sm" onClick={() => setEditorItem(null)}>
              <Plus className="h-4 w-4" /> {t('content.newItem')}
            </Button>
          }
        />
      )}

      {editorItem !== undefined ? (
        <ContentEditor
          instanceId={instance.id}
          contentType={contentType}
          itemId={editorItem}
          onClose={() => setEditorItem(undefined)}
          onSaved={() => setEditorItem(undefined)}
        />
      ) : null}
    </div>
  );
}
