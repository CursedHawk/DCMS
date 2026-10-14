import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ApiClient } from '@dcms/core';
import { sameDefinition, type SavedView, type ViewDefinition } from './model';
import type { ViewSharing } from './ViewBar';

/** Saved views of console tables (#278), from /api/admin/views, through the console's client. */
export const viewsApi = (api: ApiClient) => ({
  list: (table: string) =>
    api.get<{ views: SavedView[]; defaultView: string | null }>(`/admin/views?table=${encodeURIComponent(table)}`),
  create: (table: string, name: string, definition: ViewDefinition) =>
    api.post<SavedView>('/admin/views', { table, name, definition }),
  update: (id: string, name: string, definition: ViewDefinition) => api.put<SavedView>(`/admin/views/${id}`, { name, definition }),
  remove: (id: string) => api.del(`/admin/views/${id}`),
  share: (id: string, sharing: ViewSharing) => api.put<SavedView>(`/admin/views/${id}/sharing`, sharing),
  setDefault: (table: string, view: string | null) => api.put('/admin/views/default', { table, view }),
  audience: () =>
    api.get<{ people: { id: string; label: string }[]; roles: { id: string; label: string }[] }>('/admin/views/audience'),
});

/**
 * A table's views and what is on screen: `definition` is the working copy every control edits,
 * `dirty` says it differs from the selected view. Opens with the member's default view, unless
 * `initialViewId` (a link) names another. Built-in views (`systemViews`) are offered alongside
 * and are never saved over.
 */
export function useTableViews({
  api,
  table,
  baseline,
  systemViews = [],
  initialViewId,
  fallbackViewId,
  onViewChange,
}: {
  /** The admin API client (the console's, or a plugin screen's `usePluginApi()`). */
  api: ApiClient;
  table: string;
  /** What the table shows with no view chosen. */
  baseline: ViewDefinition;
  systemViews?: SavedView[];
  initialViewId?: string | null;
  /** Opened when neither a link nor the member's own default names a view (a maker's default view). */
  fallbackViewId?: string | null;
  onViewChange?: (id: string | null) => void;
}) {
  const qc = useQueryClient();
  const key = useMemo(() => ['table-views', table], [table]);
  const calls = useMemo(() => viewsApi(api), [api]);
  const list = useQuery({ queryKey: key, queryFn: () => calls.list(table) });
  const views = useMemo(() => [...systemViews.map((v) => ({ ...v, system: true })), ...(list.data?.views ?? [])], [systemViews, list.data]);

  const [currentId, setCurrentId] = useState<string | null>(initialViewId ?? null);
  const [definition, setDefinition] = useState<ViewDefinition>(baseline);
  const current = views.find((v) => v.id === currentId) ?? null;
  const saved = current?.definition ?? baseline;

  const select = useCallback(
    (id: string | null) => {
      const view = views.find((v) => v.id === id) ?? null;
      setCurrentId(view?.id ?? null);
      setDefinition(view ? { columns: view.definition.columns ?? [], sort: view.definition.sort ?? [], filter: view.definition.filter ?? null } : baseline);
      onViewChange?.(view?.id ?? null);
    },
    [views, baseline, onViewChange],
  );

  // Once the list is in: the view the link named, else the member's default.
  const opened = useRef(false);
  useEffect(() => {
    if (opened.current || !list.data) return;
    const wanted = initialViewId ?? list.data.defaultView ?? fallbackViewId;
    opened.current = true;
    if (wanted && views.some((v) => v.id === wanted)) select(wanted);
  }, [list.data, initialViewId, fallbackViewId, views, select]);

  const refresh = () => qc.invalidateQueries({ queryKey: key });
  const save = useMutation({
    mutationFn: () => calls.update(current!.id, current!.name, definition),
    onSuccess: refresh,
  });
  const saveAs = useMutation({
    mutationFn: (name: string) => calls.create(table, name, definition),
    onSuccess: async (view) => {
      await refresh();
      setCurrentId(view.id);
      onViewChange?.(view.id);
    },
  });
  const remove = useMutation({
    mutationFn: (id: string) => calls.remove(id),
    onSuccess: async () => {
      await refresh();
      select(null);
    },
  });
  const setDefault = useMutation({
    mutationFn: (id: string | null) => calls.setDefault(table, id),
    onSuccess: refresh,
  });
  const share = useMutation({
    mutationFn: ({ id, sharing }: { id: string; sharing: ViewSharing }) => calls.share(id, sharing),
    onSuccess: refresh,
  });

  return {
    views,
    currentId,
    current,
    defaultId: list.data?.defaultView ?? null,
    definition,
    setDefinition,
    dirty: !sameDefinition(definition, saved),
    select,
    save,
    saveAs,
    remove,
    setDefault,
    share,
    error: list.error,
    audience: calls.audience,
  };
}

export type TableViews = ReturnType<typeof useTableViews>;
