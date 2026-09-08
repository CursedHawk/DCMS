import { useNavigate } from '@tanstack/react-router';
import { useEffect } from 'react';
import { CenteredSpinner, usePermissions } from '@dcms/ui';
import { can } from '../../lib/permissions';
import { SETTINGS_SECTIONS } from '../../app/routeGuards';

/**
 * `/settings` itself, which is not a page: it forwards to the first section this caller may open.
 *
 * <p>Permission-aware, and that is the whole reason it is a component rather than a router-level
 * redirect. Somebody who holds only `audit:read` should land on the audit log, not on a refusal
 * for a General page they were sent to because it happens to be first in the list. The router's
 * `beforeLoad` runs before permissions have been fetched, so it cannot know.</p>
 *
 * <p>There is always somewhere to go: the API reference names no permission. If that ever
 * changes, this renders a spinner forever, which is why the fallback is stated rather than
 * assumed.</p>
 */
export function SettingsIndex() {
  const navigate = useNavigate();
  const me = usePermissions();

  useEffect(() => {
    if (!me) return;
    const first = SETTINGS_SECTIONS.find((s) => !s.perm || can(me, s.perm));
    if (first) void navigate({ to: first.to as string, replace: true });
  }, [me, navigate]);

  return <CenteredSpinner />;
}
