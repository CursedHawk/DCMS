import type { TagQueryMap } from '@dcms/ui';

/**
 * What each server-side resource tag makes stale in this app.
 *
 * <p>These are key <b>prefixes</b>. react-query matches by prefix, so `['media']` covers
 * `['media', folderId]` and every other variation of the list without the server knowing any
 * of them — which is the point of tagging a class of data rather than a query.</p>
 *
 * <p>Only the tags this console actually shows need an entry. An unmapped tag is ignored
 * rather than treated as "refetch everything", so a server that grows a new tag costs an old
 * console nothing.</p>
 */
export const LIVE_QUERY_MAP: TagQueryMap = {
  content: [['content'], ['content-item'], ['content-tags']],
  media: [['media'], ['media-detail'], ['media-folders'], ['media-usage']],
  sites: [['sites'], ['site']],
  // A build changing also changes what the sites list says is deployed.
  builds: [['site-builds'], ['sites'], ['git-status'], ['git-history']],
  plugins: [['plugin-instances'], ['plugin-catalog'], ['permission-catalog']],
  domains: [['domains'], ['domain-certificates']],
  // A membership change moves permissions, so the caller's own may have just changed too.
  members: [['members'], ['roles'], ['me-permissions']],
  invitations: [['invitations']],
  forms: [['form-submissions'], ['forms-instances']],
  chat: [['chat-conversations']],
  analytics: [['analytics'], ['analytics-dimensions']],
};
