import { useNavigate, useSearch } from '@tanstack/react-router';
import { FileText, LayoutGrid, Plus, Search } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  Button,
  CenteredSpinner,
  cn,
  EmptyState,
  Input,
  Page,
  PageHeader,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Table,
  TBody,
  TD,
  TH,
  THead,
  TR,
} from '@dcms/admin-ui';
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

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * What an array of strings has to look like to be a vocabulary.
 *
 * Nothing in the data says "this field holds tags" — a media reference list is
 * an array of strings exactly like a tag list is, so without this a gallery's
 * every photo shows up in the tag filter as an asset id and buries the real
 * tags. Kept in lockstep with `LooksLikeATag` in the backend's TagQueries.
 */
function looksLikeATag(value: string): boolean {
  const text = value.trim();
  return text.length > 0 && text.length <= 64 && !GUID.test(text) && !/^(\/|https?:\/\/)/.test(text);
}

/**
 * Every tag on an item, from any tag-ish field.
 *
 * Read off the loaded rows rather than fetched, so filtering by tag costs no
 * request and the offered tags are exactly the ones this collection actually
 * has. One level of nesting because a plugin's tenant-defined fields live under
 * a single key, which is where a site's most specific tags usually are.
 */
function tagsOfItem(item: ContentItem): string[] {
  const data = (item.draft ?? {}) as Record<string, unknown>;
  const out: string[] = [];
  const collect = (value: unknown) => {
    if (!Array.isArray(value)) return;
    for (const entry of value) {
      if (typeof entry === 'string' && looksLikeATag(entry)) out.push(entry.trim());
    }
  };
  for (const value of Object.values(data)) {
    collect(value);
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      for (const nested of Object.values(value as Record<string, unknown>)) collect(nested);
    }
  }
  return out;
}

function itemTitle(item: ContentItem, titleField: string | undefined): string {
  const raw = titleField ? item.draft?.[titleField] : undefined;
  const text = typeof raw === 'string' ? raw.trim() : '';
  return text || item.slug;
}

/*
 * "Scheduled" is not a ContentStatus — the enum is Draft/Published/Archived, and
 * an item waiting to go live is genuinely still a draft. The list derives it from
 * the pending schedule the server reports, so the status filter (which has always
 * offered "Scheduled") actually matches something.
 *
 * An already-published item can also have a schedule queued, for a later revision;
 * it stays Published, because that is what a visitor sees right now.
 */
function effectiveStatus(item: { status: string; scheduledPublishAt?: string | null }): string {
  return item.scheduledPublishAt && item.status !== 'Published' ? 'Scheduled' : item.status;
}

function statusTone(status: string): 'success' | 'warning' | 'secondary' {
  if (status === 'Published') return 'success';
  if (status === 'Scheduled') return 'warning';
  return 'secondary';
}

export function ContentPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const instances = usePluginInstances();
  const catalog = usePluginCatalog();
  const [selected, setSelected] = useState<{ instanceId: string; type: string } | null>(null);

  // Deep link from a notification: ?instance=<pluginInstanceId>&type=<contentType>&item=<id>.
  // `strict: false` because no route here declares a search schema — these params are optional
  // everywhere and only this page reads them.
  const deepLink = useSearch({ strict: false }) as {
    instance?: string;
    type?: string;
    item?: string;
  };

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

  // Select the collection a deep link names, else default to the first one, once data lands.
  //
  // The deep link wins even over a selection the user already made, because arriving here from
  // a notification IS the user asking for that collection — but only while the link is in the
  // URL, which is why clearing it below (once the modal has been opened) hands control back.
  useEffect(() => {
    if (authorable.length === 0) return;

    const linked = deepLink.instance
      ? authorable.find((g) => g.instance.id === deepLink.instance)
      : undefined;
    if (linked) {
      const type = linked.types.find((ct) => ct.name === deepLink.type) ?? linked.types[0];
      if (type && (selected?.instanceId !== linked.instance.id || selected.type !== type.name)) {
        setSelected({ instanceId: linked.instance.id, type: type.name });
      }
      return;
    }

    if (selected) return;
    const first = authorable.find((g) => g.types.length > 0);
    if (first) setSelected({ instanceId: first.instance.id, type: first.types[0].name });
  }, [authorable, selected, deepLink.instance, deepLink.type]);

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
                openItemId={
                  deepLink.instance === activeGroup.instance.id ? deepLink.item : undefined
                }
                onDeepLinkConsumed={() =>
                  void navigate({ to: '/content' as string, search: {} as never, replace: true })
                }
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
  openItemId,
  onDeepLinkConsumed,
}: {
  instance: PluginInstance;
  contentType: ContentTypeDef;
  /** Item to open in the editor on arrival, from a notification deep link. */
  openItemId?: string;
  onDeepLinkConsumed?: () => void;
}) {
  const { t } = useTranslation();
  // includeDraft so the list can show a real title, not just the slug.
  const items = useContentItemsOfType(instance.id, contentType.name);
  const [editorItem, setEditorItem] = useState<string | null | undefined>(undefined);
  const [query, setQuery] = useState('');
  const [status, setStatus] = useState('all');
  const [tag, setTag] = useState('all');

  // Open the linked item, then strip the link out of the URL. Stripping it matters: without
  // it, closing the modal would immediately reopen it (the effect would still see the item in
  // the search params), and the back button would put the user in the same loop.
  useEffect(() => {
    if (!openItemId) return;
    setEditorItem(openItemId);
    onDeepLinkConsumed?.();
    // Deliberately keyed on the item alone: onDeepLinkConsumed is a fresh closure each render
    // and including it would re-run this on every one of them.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [openItemId]);

  const titleField = titleFieldOf(contentType);

  const all = items.data ?? [];
  // The tags this collection actually uses, most-used first — the filter offers
  // exactly what is here rather than the tenant's whole vocabulary, most of which
  // would match nothing in this list.
  const tagOptions = useMemo(() => {
    const counts = new Map<string, { label: string; count: number }>();
    for (const item of all) {
      for (const t of new Set(tagsOfItem(item).map((v) => v))) {
        const key = t.toLowerCase();
        const seen = counts.get(key);
        if (seen) seen.count += 1;
        else counts.set(key, { label: t, count: 1 });
      }
    }
    return [...counts.entries()]
      .sort((a, b) => b[1].count - a[1].count || a[1].label.localeCompare(b[1].label))
      .map(([key, v]) => ({ key, label: v.label, count: v.count }));
  }, [all]);

  const filtered = all.filter((item) => {
    if (status !== 'all' && effectiveStatus(item) !== status) return false;
    if (tag !== 'all' && !tagsOfItem(item).some((v) => v.toLowerCase() === tag)) return false;
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
        {tagOptions.length > 0 && (
          <Select value={tag} onValueChange={setTag}>
            <SelectTrigger className="w-44">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">{t('content.tags.allTags')}</SelectItem>
              {tagOptions.map((o) => (
                <SelectItem key={o.key} value={o.key}>
                  {o.label} ({o.count})
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
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
                  <Badge
                    tone={statusTone(effectiveStatus(item))}
                    title={
                      item.scheduledPublishAt
                        ? new Date(item.scheduledPublishAt).toLocaleString()
                        : undefined
                    }
                  >
                    {effectiveStatus(item)}
                  </Badge>
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
