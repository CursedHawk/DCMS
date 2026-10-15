/**
 * Saved table views (#278) in the console. The view model itself lives in the site runtime, which
 * a site's data table shares (#297); what is console-only is how a view is shared.
 */
export * from '@dcms/site-runtime/views';
import type { ViewDefinition } from '@dcms/site-runtime/views';

export interface SavedView {
  id: string;
  name: string;
  /** The caller owns it, so may change, share and delete it. */
  mine: boolean;
  /** Who owns a view shared with the caller. */
  owner?: string | null;
  definition: ViewDefinition;
  sharedWithWorkspace?: boolean;
  /** Only on the owner's own views. */
  sharedWith?: { userIds: string[]; roleIds: string[] } | null;
  /** One of the table's built-in views (a Dynamic Apps maker view): nobody edits it here. */
  system?: boolean;
}

