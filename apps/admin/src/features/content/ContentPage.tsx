import { useNavigate, useSearch } from '@tanstack/react-router';
import { AlertTriangle, FileText, LayoutGrid, Plus, Search } from 'lucide-react';
import { lazy, Suspense, useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  Button,
  CenteredSpinner,
  cn,
  type Column,
  DataTable,
  EmptyState,
  FilterBar,
  Page,
  PageHeader,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  useDebounced,
} from '@dcms/ui';
import { dateTime } from '@dcms/core';
import {
  type ContentTypeDef,
  type PluginInstance,
  usePluginCatalog,
  usePluginInstances,
} from '../plugins/api';
import { ContentEditor } from './ContentEditor';

const TypesView = lazy(() => import('./TypesView').then((m) => ({ default: m.TypesView })));
const TagsView = lazy(() => import('./TagsView').then((m) => ({ default: m.TagsView })));
import {
  type ContentRow,
  useCollectionTags,
  useContentCounts,
  useContentPage,
} from './api';

/** Turn a machine name ("blogPost", "gig_line-up") into a readable label. */
function humanize(name: string): string {
  const spaced = name
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .trim();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
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
  const [tab, setTab] = useState('items');

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

      <Tabs value={tab} onValueChange={setTab}>
        <TabsList className="mb-6">
          <TabsTrigger value="items">{t('content.title')}</TabsTrigger>
          <TabsTrigger value="types">{t('content.types.title')}</TabsTrigger>
          <TabsTrigger value="tags">{t('content.tags.title')}</TabsTrigger>
        </TabsList>

        {/* Types and tags are lazy: both are answers to "what is in this workspace", asked
            occasionally, and neither should cost the collection list anything on first paint. */}
        <TabsContent value="types">
          <Suspense fallback={<CenteredSpinner />}>
            <TypesView />
          </Suspense>
        </TabsContent>
        <TabsContent value="tags">
          <Suspense fallback={<CenteredSpinner />}>
            <TagsView />
          </Suspense>
        </TabsContent>

        <TabsContent value="items">
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
        </TabsContent>
      </Tabs>
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
  const counts = useContentCounts(instance.id);
  const countOf = (type: string) => counts.data?.[type] ?? 0;

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
              {counts.isSuccess ? (
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
  const [editorItem, setEditorItem] = useState<string | null | undefined>(undefined);
  const [query, setQuery] = useState('');
  const [status, setStatus] = useState('all');
  const [tag, setTag] = useState('all');

  // Typing is a pause, not a keystroke: without this every character is a request, the
  // answers race, and the list flickers through the results of prefixes nobody asked about.
  const search = useDebounced(query);

  const page = useContentPage(instance.id, contentType.name, { search, status, tag });
  const tags = useCollectionTags(instance.id, contentType.name);

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

  const rows = page.data?.pages.flatMap((p) => p.items) ?? [];
  const total = page.data?.pages[0]?.total ?? 0;
  const filtered = status !== 'all' || tag !== 'all' || search.trim().length > 0;

  const columns: Column<ContentRow>[] = [
    {
      id: 'title',
      header: t('content.itemTitle'),
      primary: true,
      cell: (row) => <span className="font-medium">{row.title}</span>,
      sortValue: (row) => row.title.toLowerCase(),
    },
    {
      id: 'slug',
      header: t('common.slug'),
      cell: (row) => <span className="font-mono text-xs text-muted-foreground">{row.slug}</span>,
      sortValue: (row) => row.slug,
    },
    {
      id: 'status',
      header: t('common.status'),
      cell: (row) => (
        <Badge
          tone={statusTone(effectiveStatus(row))}
          title={row.scheduledPublishAt ? new Date(row.scheduledPublishAt).toLocaleString() : undefined}
        >
          {effectiveStatus(row)}
        </Badge>
      ),
      sortValue: (row) => effectiveStatus(row),
    },
    {
      id: 'updated',
      header: t('common.updated'),
      align: 'right',
      cell: (row) => (
        <span className="text-muted-foreground tabular-nums">{dateTime(row.updatedAt)}</span>
      ),
      sortValue: (row) => row.updatedAt,
    },
  ];

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0">
          <h2 className="truncate text-lg font-semibold tracking-tight">{humanize(contentType.name)}</h2>
          <p className="truncate text-xs text-muted-foreground">
            {instance.name} · {t('content.itemCount', { count: total })}
          </p>
        </div>
        <Button size="sm" onClick={() => setEditorItem(null)}>
          <Plus className="h-4 w-4" /> {t('content.newItem')}
        </Button>
      </div>

      <FilterBar
        search={query}
        onSearchChange={setQuery}
        searchPlaceholder={t('content.searchPlaceholder')}
        activeCount={(status === 'all' ? 0 : 1) + (tag === 'all' ? 0 : 1)}
        onClear={() => {
          setStatus('all');
          setTag('all');
        }}
        clearLabel={t('common.clearFilters')}
      >
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
        {(tags.data?.length ?? 0) > 0 && (
          <Select value={tag} onValueChange={setTag}>
            <SelectTrigger className="w-44">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">{t('content.tags.allTags')}</SelectItem>
              {tags.data!.map((o) => (
                <SelectItem key={o.tag} value={o.tag}>
                  {o.tag} ({o.count})
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      </FilterBar>

      <DataTable
        rows={rows}
        columns={columns}
        rowKey={(row) => row.id}
        isLoading={page.isLoading}
        error={
          page.isError ? (
            // Distinct from the empty state on purpose: a list that failed to load and a list
            // with nothing in it look identical unless one of them says so, and "there is
            // nothing here" is the more damaging of the two to get wrong.
            <EmptyState
              icon={AlertTriangle}
              title={t('common.loadFailed')}
              action={
                <Button variant="outline" size="sm" onClick={() => void page.refetch()}>
                  {t('common.retry')}
                </Button>
              }
            />
          ) : undefined
        }
        onRowClick={(row) => setEditorItem(row.id)}
        caption={t('content.itemCount', { count: total })}
        labels={{
          loading: t('common.loading'),
          sortBy: (column) => t('common.sortBy', { column }),
        }}
        empty={
          filtered ? (
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
          )
        }
      />

      {page.hasNextPage && (
        <div className="flex justify-center">
          <Button
            variant="outline"
            size="sm"
            disabled={page.isFetchingNextPage}
            onClick={() => void page.fetchNextPage()}
          >
            {page.isFetchingNextPage ? t('common.loading') : t('content.loadMore')}
          </Button>
        </div>
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
