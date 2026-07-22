import { useQuery } from '@tanstack/react-query';
import { api } from '../../lib/api';

export interface ContentItem {
  id: string;
  contentType: string;
  slug: string;
  status: string;
  updatedAt: string;
  publishedAt?: string;
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
  versions: ContentVersion[];
}

export function useContentItems(instanceId: string | undefined) {
  return useQuery({
    queryKey: ['content', instanceId],
    enabled: !!instanceId,
    queryFn: () => api.get<ContentItem[]>(`/admin/content?instanceId=${instanceId}`),
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

export function useContentItem(id: string | undefined) {
  return useQuery({
    queryKey: ['content-item', id],
    enabled: !!id,
    queryFn: () => api.get<ContentDetail>(`/admin/content/${id}`),
  });
}
