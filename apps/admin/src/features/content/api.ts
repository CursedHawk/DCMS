import { useQuery } from '@tanstack/react-query';
import { api } from '../../lib/api';

export interface ContentItem {
  id: string;
  contentType: string;
  slug: string;
  status: string;
  updatedAt: string;
  publishedAt?: string;
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

export function useContentItem(id: string | undefined) {
  return useQuery({
    queryKey: ['content-item', id],
    enabled: !!id,
    queryFn: () => api.get<ContentDetail>(`/admin/content/${id}`),
  });
}
