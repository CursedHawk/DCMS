import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../lib/api';

export type MediaCategory = 'Image' | 'Video' | 'Audio' | 'File';
export type MediaStatus = 'Uploaded' | 'Processing' | 'Ready' | 'Failed';

/** Sentinel folder id for the top-level "unfiled" view (assets with no folder). */
export const ROOT_FOLDER = '00000000-0000-0000-0000-000000000000';

export interface MediaAsset {
  id: string;
  category: MediaCategory;
  fileName: string;
  status: MediaStatus;
  sizeBytes: number;
  folderId: string | null;
  createdAt: string;
  /** Total bytes of derived renditions (the "compressed" footprint). */
  variantBytes: number;
  variantCount: number;
  width?: number | null;
  height?: number | null;
}

export interface MediaVariant {
  kind: string;
  width?: number;
  height?: number;
  sizeBytes: number;
  contentType: string;
}

export interface MediaDetail {
  id: string;
  category: MediaCategory;
  fileName: string;
  contentType: string;
  status: MediaStatus;
  error?: string;
  sizeBytes: number;
  folderId: string | null;
  createdAt: string;
  variants: MediaVariant[];
}

export interface MediaFolder {
  id: string;
  name: string;
  parentId: string | null;
  createdAt: string;
  assetCount: number;
}

export interface MediaUsage {
  originalBytes: number;
  variantBytes: number;
  totalBytes: number;
  assetCount: number;
  folderCount: number;
  byCategory: { category: MediaCategory; count: number; originalBytes: number }[];
}

/**
 * Lists assets. `folderId` undefined → the whole library; `ROOT_FOLDER` → only
 * unfiled assets; a folder id → that folder's assets.
 */
export function useMedia(folderId?: string) {
  const qs = folderId !== undefined ? `?folderId=${folderId}` : '';
  return useQuery({
    queryKey: ['media', folderId ?? 'all'],
    queryFn: () => api.get<MediaAsset[]>(`/admin/media${qs}`),
    /*
     * A fallback, not the mechanism.
     *
     * media-worker publishes `media.processed` / `media.failed`, admin-api turns those into a
     * `media` resource change, and the hub invalidates this query the moment a transcode
     * finishes — so the grid is normally correct within a few hundred milliseconds without any
     * polling at all. This interval only covers the case where the socket is down, which is
     * why it is 15 seconds rather than the 3 it used to be: it is no longer racing the server,
     * it is insuring against a lost connection.
     */
    refetchInterval: (q) =>
      (q.state.data ?? []).some((a) => a.status === 'Processing' || a.status === 'Uploaded')
        ? 15_000
        : false,
  });
}

export function useMediaDetail(id: string | undefined) {
  return useQuery({
    queryKey: ['media-detail', id],
    enabled: !!id,
    queryFn: () => api.get<MediaDetail>(`/admin/media/${id}`),
    // Same reasoning as the list above: the hub is the mechanism, this is the insurance.
    refetchInterval: (q) =>
      q.state.data && (q.state.data.status === 'Processing' || q.state.data.status === 'Uploaded')
        ? 15_000
        : false,
  });
}

export function useMediaFolders() {
  return useQuery({
    queryKey: ['media-folders'],
    queryFn: () => api.get<MediaFolder[]>('/admin/media/folders'),
  });
}

export function useMediaUsage() {
  return useQuery({
    queryKey: ['media-usage'],
    queryFn: () => api.get<MediaUsage>('/admin/media/usage'),
  });
}

/** Invalidates every media-related query after a mutation. */
function useInvalidateMedia() {
  const qc = useQueryClient();
  return () =>
    Promise.all([
      qc.invalidateQueries({ queryKey: ['media'] }),
      qc.invalidateQueries({ queryKey: ['media-folders'] }),
      qc.invalidateQueries({ queryKey: ['media-usage'] }),
      qc.invalidateQueries({ queryKey: ['media-detail'] }),
    ]);
}

export function useCreateFolder() {
  const invalidate = useInvalidateMedia();
  return useMutation({
    mutationFn: (body: { name: string; parentId?: string | null }) =>
      api.post<MediaFolder>('/admin/media/folders', body),
    onSuccess: invalidate,
  });
}

export function useRenameFolder() {
  const invalidate = useInvalidateMedia();
  return useMutation({
    mutationFn: ({ id, name }: { id: string; name: string }) =>
      api.patch<void>(`/admin/media/folders/${id}`, { name }),
    onSuccess: invalidate,
  });
}

export function useDeleteFolder() {
  const invalidate = useInvalidateMedia();
  return useMutation({
    mutationFn: (id: string) => api.del<void>(`/admin/media/folders/${id}`),
    onSuccess: invalidate,
  });
}

export function useRenameAsset() {
  const invalidate = useInvalidateMedia();
  return useMutation({
    mutationFn: ({ id, fileName }: { id: string; fileName: string }) =>
      api.patch<void>(`/admin/media/${id}`, { fileName }),
    onSuccess: invalidate,
  });
}

export function useMoveAssets() {
  const invalidate = useInvalidateMedia();
  return useMutation({
    mutationFn: ({ ids, folderId }: { ids: string[]; folderId: string | null }) =>
      api.post<{ moved: number }>('/admin/media/move', { ids, folderId }),
    onSuccess: invalidate,
  });
}

export function useDeleteAssets() {
  const invalidate = useInvalidateMedia();
  return useMutation({
    mutationFn: (ids: string[]) => api.post<{ deleted: number }>('/admin/media/delete', { ids }),
    onSuccess: invalidate,
  });
}

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

/** Short, locale-aware absolute date (e.g. "11 Aug 2026"). */
export function formatDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
}
