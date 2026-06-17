import { useQuery } from '@tanstack/react-query';
import { api } from '../../lib/api';

export type MediaCategory = 'Image' | 'Video' | 'Audio' | 'File';
export type MediaStatus = 'Uploaded' | 'Processing' | 'Ready' | 'Failed';

export interface MediaAsset {
  id: string;
  category: MediaCategory;
  fileName: string;
  status: MediaStatus;
  sizeBytes: number;
}

export interface MediaVariant {
  kind: string;
  width?: number;
  height?: number;
  sizeBytes: number;
}
export interface MediaDetail extends MediaAsset {
  contentType: string;
  error?: string;
  variants: MediaVariant[];
}

export function useMedia() {
  return useQuery({
    queryKey: ['media'],
    queryFn: () => api.get<MediaAsset[]>('/admin/media'),
    // Poll while anything is still processing so the grid updates itself.
    refetchInterval: (q) =>
      (q.state.data ?? []).some((a) => a.status === 'Processing' || a.status === 'Uploaded')
        ? 3000
        : false,
  });
}

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
