import { useInfiniteQuery, useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../lib/api';

export interface ContentItem {
  id: string;
  contentType: string;
  slug: string;
  status: string;
  updatedAt: string;
  publishedAt?: string;
  /** When a queued publish will fire; null when nothing is scheduled. */
  scheduledPublishAt?: string | null;
  /** Only present when the list was requested with the draft data included. */
  draft?: Record<string, unknown> | null;
}

export interface ContentVersion {
  id: string;
  versionNo: number;
  createdAt: string;
  published: boolean;
}

export interface ContentDetail {
  id: string;
  contentType: string;
  slug: string;
  status: string;
  draft: Record<string, unknown> | null;
  /** When a pending scheduled publish will fire, or null if none is queued. */
  scheduledPublishAt?: string | null;
  versions: ContentVersion[];
}

export interface TagSuggestion {
  tag: string;
  count: number;
  /** Where the tag is used across the tenant, most used first. */
  occurrences?: { instanceSlug: string; contentType: string; field: string; count: number }[];
}

/**
 * How many items of each content type an instance holds, for the collection rail.
 *
 * <p>A count, not a list. The rail used to fetch every item of every instance in it — for all
 * of them at once, whether or not the collection was open — and count in the browser.</p>
 */
export function useContentCounts(instanceId: string | undefined) {
  return useQuery({
    queryKey: ['content', instanceId, 'counts'],
    enabled: !!instanceId,
    queryFn: () => api.get<Record<string, number>>(`/admin/content/counts?instanceId=${instanceId}`),
  });
}

/**
 * Items of one content type in an instance, with their draft data — used by
 * editors that reference other content (e.g. picking crew members for a gig).
 */
export function useContentItemsOfType(instanceId: string | undefined, contentType: string | undefined) {
  return useQuery({
    queryKey: ['content', instanceId, contentType, 'with-draft'],
    enabled: !!instanceId && !!contentType,
    queryFn: () =>
      api.get<ContentItem[]>(
        `/admin/content?instanceId=${instanceId}&contentType=${encodeURIComponent(contentType!)}&includeDraft=true`,
      ),
  });
}

/** One row of a collection list. No draft: the server projects the title instead. */
export interface ContentRow {
  id: string;
  contentType: string;
  slug: string;
  /** The item's first textual field, tags stripped, falling back to the slug. */
  title: string;
  status: string;
  updatedAt: string;
  publishedAt?: string | null;
  scheduledPublishAt?: string | null;
}

export interface ContentPage {
  items: ContentRow[];
  /** Opaque; hand it back to continue. Null when this was the last page. */
  nextCursor: string | null;
  /** How many items match the filters, not how many are left below the scroll. */
  total: number;
}

export interface ContentListFilters {
  search: string;
  /** A ContentStatus, or the synthetic `Scheduled`, or `all`. */
  status: string;
  /** A tag, or `all`. */
  tag: string;
}

/**
 * One collection, a page at a time, filtered by the server.
 *
 * <p>This replaced fetching every item in the collection <i>with its full draft</i> — the only
 * way the browser could show a real title — and filtering client-side. That payload grows with
 * what authors have written rather than with the row count, so it degraded quietly and in
 * proportion to how much the tenant had used the product.</p>
 *
 * <p>The filters are in the query key, so changing one is a new query rather than a refetch:
 * react-query keeps the old page rendered while the new one loads instead of blanking the
 * table on every keystroke.</p>
 */
export function useContentPage(
  instanceId: string | undefined,
  contentType: string | undefined,
  filters: ContentListFilters,
) {
  return useInfiniteQuery({
    queryKey: ['content', instanceId, contentType, 'page', filters],
    enabled: !!instanceId && !!contentType,
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) => {
      const params = new URLSearchParams({
        instanceId: instanceId!,
        contentType: contentType!,
      });
      if (filters.search.trim()) params.set('search', filters.search.trim());
      if (filters.status !== 'all') params.set('status', filters.status);
      if (filters.tag !== 'all') params.set('tag', filters.tag);
      if (pageParam) params.set('cursor', pageParam);
      return api.get<ContentPage>(`/admin/content/page?${params.toString()}`);
    },
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
}

/**
 * The tags one collection actually uses, most-used first.
 *
 * <p>From the server rather than from the loaded rows, which is what the filter used to do.
 * Reading them off a page would offer only the tags on that page — so a tag would appear in
 * the filter, be selected, and then narrow the list to nothing that had scrolled past.</p>
 */
export function useCollectionTags(instanceId: string | undefined, contentType: string | undefined) {
  return useQuery({
    queryKey: ['content-tags', instanceId, contentType, 'collection'],
    enabled: !!instanceId && !!contentType,
    staleTime: 60_000,
    queryFn: () =>
      api.get<TagSuggestion[]>(
        `/admin/content/tags?instanceId=${instanceId}&contentType=${encodeURIComponent(contentType!)}`,
      ),
  });
}

export function useContentItem(id: string | undefined) {
  return useQuery({
    queryKey: ['content-item', id],
    enabled: !!id,
    queryFn: () => api.get<ContentDetail>(`/admin/content/${id}`),
  });
}

/**
 * Every tag this tenant uses anywhere, most-used first.
 *
 * The tenant, not the field, is the right scope for reuse: a tag is a way for a
 * reader to move between things, and "live" on an event and "live" on a gallery
 * item are the same word to them. One query, shared by every tag input in the
 * app — which is what makes the vocabulary actually reusable rather than
 * re-invented per collection.
 */
export function useTenantTags() {
  return useQuery({
    queryKey: ['content-tags', 'tenant'],
    staleTime: 60_000,
    queryFn: () => api.get<TagSuggestion[]>('/admin/content/tags'),
  });
}

/**
 * The tag suggestions for each named Tags field, keyed by field name.
 *
 * Two sources, in order: the field's own vocabulary first, then everything else
 * the tenant uses. The field's own tags lead because they are the likely answer
 * — a genres field should offer genres before it offers the marketing tags from
 * a blog — but the rest stays reachable, so a tag coined on one collection can be
 * reused on another instead of being retyped and split.
 *
 * One request per field plus the tenant-wide one. `useQueries` because the field
 * list is data, so the number of hooks would otherwise vary between renders.
 */
export function useTagVocabularies(
  instanceId: string | undefined,
  contentType: string | undefined,
  fields: string[],
): Record<string, string[]> {
  const results = useQueries({
    queries: fields.map((field) => ({
      queryKey: ['content-tags', instanceId, contentType, field],
      enabled: !!instanceId && !!contentType,
      staleTime: 60_000,
      queryFn: () =>
        api.get<TagSuggestion[]>(
          `/admin/content/tags?instanceId=${instanceId}&contentType=${encodeURIComponent(contentType!)}&field=${encodeURIComponent(field)}`,
        ),
    })),
  });
  const tenantWide = useTenantTags();

  const out: Record<string, string[]> = {};
  fields.forEach((field, i) => {
    const own = (results[i]?.data ?? []).map((s) => s.tag);
    const seen = new Set(own.map((t) => t.toLowerCase()));
    const rest = (tenantWide.data ?? [])
      .map((s) => s.tag)
      .filter((t) => !seen.has(t.toLowerCase()));
    out[field] = [...own, ...rest];
  });
  return out;
}

/** One row of the workspace-wide publishing queue. */
export interface ScheduledItem {
  scheduleId: string;
  itemId: string;
  instanceId: string;
  /** The tenant's own name for the collection — "Press room", not "blog". */
  instanceName: string;
  pluginId: string;
  contentType: string;
  slug: string;
  /** The title of the version that is queued, not of the draft being written now. */
  title: string;
  status: string;
  publishAt: string;
}

/**
 * Everything queued to publish, across every collection.
 *
 * <p>The scheduler has worked since it shipped and its queue was never visible: an author could
 * see "Scheduled" beside the one item they had open, and had no way to answer "what goes out
 * this week". Every other content query is scoped to one plugin instance because that is how
 * the console browses; this one deliberately is not.</p>
 */
export function useScheduledContent(enabled = true) {
  return useQuery({
    queryKey: ['content-scheduled'],
    enabled,
    queryFn: () => api.get<{ items: ScheduledItem[] }>('/admin/content/scheduled'),
  });
}

/** What a bulk action does to each selected item. */
export type BulkVerb = 'publish' | 'unpublish' | 'delete' | 'cancelSchedule';

export interface BulkResult {
  done: string[];
  failed: { id: string; message: string }[];
}

const bulkRequest = (verb: BulkVerb, id: string): Promise<unknown> => {
  switch (verb) {
    case 'publish':
      // No body: publish what is already the current draft. The editor sends its unsaved edits
      // instead, which is exactly the difference between publishing from a form and from a list.
      return api.post(`/admin/content/${id}/publish`, undefined);
    case 'unpublish':
      return api.post(`/admin/content/${id}/unpublish`, undefined);
    case 'cancelSchedule':
      return api.del(`/admin/content/${id}/schedule`);
    case 'delete':
      return api.del(`/admin/content/${id}`);
  }
};

/**
 * One action applied to a selection.
 *
 * <p><b>There is no bulk endpoint, and this does not pretend there is one.</b> Each item is its
 * own request, because each is its own audit record, its own outbox row and its own permission
 * check — a server-side batch would have to reproduce all three and would make a partial
 * failure invisible. So the failures are collected rather than thrown: publishing eight items
 * and having one refuse should report "seven published, one refused" and name it, not roll back
 * the seven and not claim eight.</p>
 *
 * <p>Requests go out a few at a time. All fifty at once is a burst that the API's own rate
 * limiting would answer with 429s, which would look to the reader like the items failed.</p>
 */
export function useBulkContentAction() {
  const qc = useQueryClient();

  return useMutation({
    mutationFn: async ({ verb, ids }: { verb: BulkVerb; ids: string[] }): Promise<BulkResult> => {
      const done: string[] = [];
      const failed: { id: string; message: string }[] = [];
      const queue = [...ids];

      const worker = async () => {
        for (let id = queue.shift(); id !== undefined; id = queue.shift()) {
          try {
            await bulkRequest(verb, id);
            done.push(id);
          } catch (e) {
            failed.push({ id, message: e instanceof Error ? e.message : String(e) });
          }
        }
      };

      await Promise.all(Array.from({ length: Math.min(4, ids.length) }, worker));
      return { done, failed };
    },
    onSettled: () =>
      Promise.all([
        qc.invalidateQueries({ queryKey: ['content'] }),
        qc.invalidateQueries({ queryKey: ['content-item'] }),
        qc.invalidateQueries({ queryKey: ['content-scheduled'] }),
        qc.invalidateQueries({ queryKey: ['content-tags'] }),
      ]),
  });
}
