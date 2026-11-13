import { api } from '../../lib/api';

/**
 * The workspace knowledge store (ADR 0034): past decisions, setups, information and archives of
 * compacted conversations, at folder-like paths. Read by agents only when they ask for it.
 */

export type KnowledgeSource = 'agent' | 'member' | 'compaction';

export interface KnowledgeSummary {
  id: string;
  /** Null for a workspace-wide entry. */
  siteId: string | null;
  path: string;
  title: string;
  description: string;
  source: KnowledgeSource;
  updatedAt: string;
}

export interface KnowledgeEntry extends KnowledgeSummary {
  body: string;
  conversationId: string | null;
  fromSeq: number | null;
  toSeq: number | null;
  createdAt: string;
}

export interface KnowledgeListing {
  folder: string;
  /** Immediate subfolders of `folder`, without a trailing slash. */
  folders: string[];
  entries: KnowledgeSummary[];
}

/**
 * Which entries a caller sees: a site's agent sees workspace entries and its own site's; the
 * console assistant and the console page see every scope.
 */
export type KnowledgeScope = { siteId: string } | { allSites: true } | Record<string, never>;

export const knowledgeKey = 'ai-knowledge';

function query(params: Record<string, string | undefined>, scope: KnowledgeScope): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value) search.set(key, value);
  if ('siteId' in scope) search.set('siteId', scope.siteId);
  if ('allSites' in scope) search.set('allSites', 'true');
  const text = search.toString();
  return text ? `?${text}` : '';
}

export const knowledgeApi = {
  list: (scope: KnowledgeScope, folder?: string) =>
    api.get<KnowledgeListing>(`/admin/ai/knowledge${query({ folder }, scope)}`),
  /** Every entry in the scope, for a tree; the server caps it. */
  tree: (scope: KnowledgeScope) => api.get<KnowledgeListing>(`/admin/ai/knowledge${query({ recursive: 'true' }, scope)}`),
  search: (scope: KnowledgeScope, q: string, folder?: string) =>
    api.get<KnowledgeListing>(`/admin/ai/knowledge${query({ q, folder }, scope)}`),
  /** The entry at `path` in the given site, falling back to the workspace-wide one. */
  read: (path: string, siteId?: string | null) =>
    api.get<KnowledgeEntry>(`/admin/ai/knowledge/entry${query({ path, siteId: siteId ?? undefined }, {})}`),
  save: (entry: {
    siteId: string | null;
    path: string;
    title: string;
    description: string;
    body: string;
    source?: KnowledgeSource;
    conversationId?: string | null;
    fromSeq?: number | null;
    toSeq?: number | null;
    expectedUpdatedAt?: string | null;
  }) => api.put<KnowledgeSummary>('/admin/ai/knowledge/entry', entry),
  remove: (id: string) => api.del(`/admin/ai/knowledge/${id}`),
  /** Passages of a conversation's stored turns that mention `q`, within a seq range. */
  excerpts: (conversationId: string, q: string, from?: number | null, to?: number | null) =>
    api.get<{ excerpts: { seq: number; role: string; excerpt: string }[] }>(
      `/admin/ai/conversations/${conversationId}/excerpts${query(
        { q, from: from == null ? undefined : String(from), to: to == null ? undefined : String(to) },
        {},
      )}`,
    ),
};

/** A path as the server accepts it: lower-case `[a-z0-9-]` segments joined by `/`. */
export function normalisePath(path: string): string {
  return path
    .toLowerCase()
    .split('/')
    .map((segment) =>
      segment
        .normalize('NFKD')
        .replace(/[̀-ͯ]/g, '')
        .replace(/[^a-z0-9-]+/g, '-')
        .replace(/-+/g, '-')
        .replace(/^-|-$/g, ''),
    )
    .filter(Boolean)
    .join('/');
}
