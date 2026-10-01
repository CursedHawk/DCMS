import { useQuery } from '@tanstack/react-query';
import { instancePath, usePluginApi } from '@dcms/plugin-ui';

/*
 * The UI talks to three kinds of the plugin's own server code, through the console's API client
 * (signed in, current workspace, refreshed tokens — none of it this plugin's business):
 *   - its host route:        GET  /admin/guestbook/overview          (GuestbookPlugin.MapHostEndpoints)
 *   - its instance routes:   POST /admin/plugins/{slug}/entries/{id}/approve (MapAdminEndpoints)
 *   - its data set:          GET  /admin/plugins/{slug}/_data/entries … (EntriesDataSet, served by the platform)
 */

export interface GuestbookSummary {
  instanceId: string;
  slug: string;
  name: string;
  pending: number;
  approved: number;
}

export interface Overview {
  guestbooks: GuestbookSummary[];
  blogPostsSeen: number;
}

export interface EntryRow {
  key: string;
  title: string | null;
  values: {
    name: string;
    message: string;
    status: 'waiting' | 'shown' | 'turned down';
    signedAt: string;
    reply: string | null;
  };
}

export type StatusFilter = 'pending' | 'approved' | 'rejected';

export function useOverview() {
  const api = usePluginApi();
  return useQuery({ queryKey: ['guestbook', 'overview'], queryFn: () => api.get<Overview>('/admin/guestbook/overview') });
}

export function useEntries(slug: string, status: StatusFilter) {
  const api = usePluginApi();
  return useQuery({
    queryKey: ['guestbook', 'entries', slug, status],
    queryFn: () =>
      api.get<{ rows: EntryRow[]; total: number }>(
        `${instancePath(slug, '/_data/entries')}?pageSize=60&f.status=${status}`,
      ),
  });
}

export function useGuestbookApi() {
  const api = usePluginApi();
  return {
    /** The plugin's own admin route, audited as plugin.sample-guestbook.entry.approved. */
    approve: (slug: string, id: string) => api.post(instancePath(slug, `/entries/${encodeURIComponent(id)}/approve`)),
    /** A data-set action: the platform checks `moderate`, runs EntriesDataSet, audits plugin.data.actioned. */
    reject: (slug: string, id: string) =>
      api.post(instancePath(slug, '/_data/entries/actions/reject'), { keys: [id] }),
    /** Enqueues the export job (dcms.jobs); the CSV lands in the instance's Files. */
    exportEntries: (slug: string, email?: string) => api.post(instancePath(slug, '/export'), { email: email || null }),
  };
}
