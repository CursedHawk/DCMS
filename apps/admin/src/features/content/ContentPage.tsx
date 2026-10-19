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
  cleanFilter,
  TableViewControls,
  useDebounced,
  useTableViews,
  type ViewDefinition,
} from '@dcms/ui';
import { dateTime } from '@dcms/core';
import {
  type ContentTypeDef,
  type PluginInstance,
  usePluginCatalog,
  usePluginInstances,
} from '../plugins/api';
import { api } from '../../lib/api';
import { BulkActions } from './BulkActions';
import {
  BUILT_IN_COLUMNS,
  CellValue,
  collectionColumnFields,
  collectionDataFields,
  collectionFilterFields,
  columnDataFields,
  humanize,
} from './collectionView';
import { ContentEditor } from './ContentEditor';

const TypesView = lazy(() => import('./TypesView').then((m) => ({ default: m.TypesView })));
const TagsView = lazy(() => import('./TagsView').then((m) => ({ default: m.TagsView })));
const ScheduledView = lazy(() =>
  import('./ScheduledView').then((m) => ({ default: m.ScheduledView })),
);
import {
  type ContentRow,
  type ContentSort,
  type ContentSortColumn,
  useCollectionTags,
  useContentCounts,
  useContentPage,
} from './api';


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
  const deepLink = useSearch({ strict: false }) as ContentSearch;

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
      // A deep link names an item to open, so it has to bring the collection tab with it.
      // Without this a row clicked in the publishing queue selects a collection nobody can see
      // and the click looks like it did nothing. Only then: the collection stays in the URL
      // while it is open, and a refetch must not pull the reader off the tab they chose.
      if (deepLink.item) setTab('items');
      return;
    }

    if (selected) return;
    const first = authorable.find((g) => g.types.length > 0);
    if (first) setSelected({ instanceId: first.instance.id, type: first.types[0].name });
  }, [authorable, selected, deepLink.instance, deepLink.type, deepLink.item]);

  const activeGroup = authorable.find((g) => g.instance.id === selected?.instanceId);
  const activeType = activeGroup?.types.find((ct) => ct.name === selected?.type);

  if (instances.isLoading || catalog.isLoading) {
    return (
      <Page>
        <PageHeader title={t('content.title')} description={t('content.subtitle')} />
        <CenteredSpinner />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader title={t('content.title')} description={t('content.subtitle')} />

      <Tabs value={tab} onValueChange={setTab}>
        <TabsList className="mb-6">
          <TabsTrigger value="items">{t('content.title')}</TabsTrigger>
          <TabsTrigger value="scheduled">{t('content.scheduledView.tab')}</TabsTrigger>
          <TabsTrigger value="types">{t('content.types.title')}</TabsTrigger>
          <TabsTrigger value="tags">{t('content.tags.title')}</TabsTrigger>
        </TabsList>

        {/* The three side tabs are lazy: each answers "what is in this workspace" rather than
            "what am I editing", they are asked occasionally, and none of them should cost the
            collection list anything on first paint. */}
        <TabsContent value="scheduled">
          <Suspense fallback={<CenteredSpinner />}>
            <ScheduledView />
          </Suspense>
        </TabsContent>
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
        <div className="grid grid-cols-1 gap-6 lg:grid-cols-[260px_minmax(0,1fr)]">
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
                  // Into the URL, so a link or the back button returns to this collection. Its
                  // filters and order belong to the collection left behind, so they go.
                  onSelect={(type) =>
                    void navigate({
                      to: '/content' as string,
                      search: { instance: group.instance.id, type } as never,
                    })
                  }
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
                  void navigate({
                    to: '/content' as string,
                    search: ((prev: ContentSearch) => ({ ...prev, item: undefined })) as never,
                    replace: true,
                  })
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
  const navigate = useNavigate();
  const [editorItem, setEditorItem] = useState<string | null | undefined>(undefined);
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set());

  /*
   * Filters and order live in the URL, so a shared link, a reload and the back button all land
   * on the same list. Replace, not push: a history entry per keystroke or per click on a column
   * header would make Back step through every intermediate list.
   */
  const params = useSearch({ strict: false }) as ContentSearch;
  const setParams = (next: Partial<ContentSearch>) =>
    void navigate({
      to: '/content' as string,
      search: ((prev: ContentSearch) => ({ ...prev, ...next })) as never,
      replace: true,
    });
  const status = params.status ?? 'all';
  const tag = params.tag ?? 'all';

  /*
   * Saved views (#278): the view is what the list shows — its columns, order and filter — and
   * `views.definition` is the working copy every control edits. A link's own sort still wins
   * over no view, so a shared "sorted by title" link keeps meaning that.
   */
  const dataFields = useMemo(() => collectionDataFields(instance, contentType), [instance, contentType]);
  const [baseline] = useState<ViewDefinition>(() => ({
    columns: [...BUILT_IN_COLUMNS],
    sort: isSortColumn(params.sort) ? [{ field: sortFieldOf(params.sort), direction: params.dir === 'desc' ? 'desc' : 'asc' }] : [],
  }));
  const views = useTableViews({
    api,
    table: `content:${instance.slug}:${contentType.name}`,
    baseline,
    initialViewId: params.view,
    // The view carries its own order; a link's sort only stood in while there was none.
    onViewChange: (id) => setParams({ view: id ?? undefined, sort: undefined, dir: undefined }),
  });
  const viewSort = views.definition.sort[0];
  const sort: ContentSort | null = viewSort
    ? { column: sortColumnOf(viewSort.field), direction: viewSort.direction }
    : null;
  const viewFilter = cleanFilter(views.definition.filter);
  const fieldColumns = views.definition.columns.filter((c) => columnDataFields(dataFields).some((f) => f.key === c));

  // Typing is a pause, not a keystroke: without this every character is a request, the
  // answers race, and the list flickers through the results of prefixes nobody asked about.
  const [query, setQuery] = useState(params.q ?? '');
  const search = useDebounced(query);
  useEffect(() => {
    if (search !== (params.q ?? '')) setParams({ q: search || undefined });
    // Keyed on the debounced value: the URL follows the search box, not the other way round.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search]);

  const page = useContentPage(instance.id, contentType.name, {
    search,
    status,
    tag,
    sort,
    filter: viewFilter,
    columns: fieldColumns,
  });
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

  // Memoised, because `visible` below depends on it and is itself a prop. A fresh array on
  // every render would make the selection a fresh prop on every keystroke in the search box —
  // the same shape that took the media library down through `useAiPageContext`.
  const rows = useMemo(() => page.data?.pages.flatMap((p) => p.items) ?? [], [page.data]);
  const total = page.data?.pages[0]?.total ?? 0;
  const filtered = status !== 'all' || tag !== 'all' || search.trim().length > 0 || !!viewFilter;

  /*
   * Only what is still on screen counts as selected.
   *
   * Changing a filter does not clear the selection — narrowing to Drafts, ticking three, then
   * widening again should still have three ticked — but a bulk action must never reach an item
   * the reader can no longer see. Memoised because it is handed to `BulkActions`, and an array
   * rebuilt every render there would be a fresh prop on every keystroke in the search box.
   */
  const visible = useMemo(() => {
    const onScreen = new Set(rows.map((r) => r.id));
    return [...selected].filter((id) => onScreen.has(id));
  }, [rows, selected]);

  const builtIn: Column<ContentRow>[] = [
    {
      id: 'title',
      header: t('content.itemTitle'),
      primary: true,
      cell: (row) => <span className="font-medium">{row.title}</span>,
      sortable: true,
    },
    {
      id: 'slug',
      header: t('common.slug'),
      cell: (row) => <span className="font-mono text-xs text-muted-foreground">{row.slug}</span>,
      sortable: true,
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
    },
    {
      id: 'updated',
      header: t('common.updated'),
      align: 'right',
      cell: (row) => (
        <span className="text-muted-foreground tabular-nums">{dateTime(row.updatedAt)}</span>
      ),
      sortable: true,
    },
  ];
  // The view's columns in its order: the built-in ones, then any of the collection's fields.
  const columns: Column<ContentRow>[] = views.definition.columns.flatMap((id): Column<ContentRow>[] => {
    const own = builtIn.find((c) => c.id === id);
    if (own) return [own];
    const field = columnDataFields(dataFields).find((f) => f.key === id);
    if (!field) return [];
    return [
      {
        id: field.key,
        header: field.label,
        cell: (row) => <CellValue kind={field.kind} value={row.values?.[field.key]} />,
        sortable: field.sortable !== false,
      },
    ];
  });
  if (columns.length === 0) columns.push(builtIn[0]);

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
        onClear={() => setParams({ status: undefined, tag: undefined })}
        clearLabel={t('common.clearFilters')}
      >
        <Select value={status} onValueChange={(v) => setParams({ status: v === 'all' ? undefined : v })}>
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
          <Select value={tag} onValueChange={(v) => setParams({ tag: v === 'all' ? undefined : v })}>
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

      <TableViewControls
        views={views}
        fields={collectionFilterFields(dataFields, t)}
        columnFields={collectionColumnFields(dataFields, t)}
      />

      {visible.length > 0 ? (
        <BulkActions
          ids={visible}
          onDone={() => setSelected(new Set())}
          onClear={() => setSelected(new Set())}
        />
      ) : null}

      <DataTable
        rows={rows}
        columns={columns}
        rowKey={(row) => row.id}
        isLoading={page.isLoading}
        // Ordered by the server: the list is paged, and sorting what has loaded would order
        // only that.
        sort={viewSort ? { columnId: viewSort.field, direction: viewSort.direction } : null}
        onSortChange={(next) => {
          views.setDefinition((d) => ({ ...d, sort: next ? [{ field: next.columnId, direction: next.direction }] : [] }));
          setParams({ sort: next ? sortColumnOf(next.columnId) : undefined, dir: next?.direction });
        }}
        selection={{ selected, onChange: setSelected }}
        labels={{
          loading: t('common.loading'),
          sortBy: (column) => t('common.sortBy', { column }),
          selectRow: t('common.selectRow'),
          selectAll: t('common.selectAll'),
        }}
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

/** What `/content` keeps in its URL: the open collection, an item to open, and the list's filters. */
interface ContentSearch {
  instance?: string;
  type?: string;
  item?: string;
  q?: string;
  status?: string;
  tag?: string;
  sort?: string;
  dir?: string;
  /** The saved view on screen (#278). */
  view?: string;
}

function isSortColumn(value: string | undefined): value is ContentSortColumn {
  return value === 'title' || value === 'slug' || value === 'updated' || (!!value && value.startsWith('field:'));
}

/** A column id as the server's order: built-in orders by name, a collection field as `field:{key}`. */
function sortColumnOf(columnId: string): ContentSortColumn {
  return columnId === 'title' || columnId === 'slug' || columnId === 'updated' ? columnId : `field:${columnId}`;
}

/** The column an order sorts: the inverse of `sortColumnOf`. */
function sortFieldOf(column: ContentSortColumn): string {
  return column.startsWith('field:') ? column.slice(6) : column;
}
