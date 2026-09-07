import { useCan, useIsSuperAdmin } from './context';

/**
 * Renders its children only for a caller who holds the permission.
 *
 * For things that are *absent* rather than *refused* — a whole section of a page, a menu
 * entry, a column of a table. Where the control would otherwise be visible and clickable,
 * prefer rendering it disabled with `PermissionTooltip`: hiding a button teaches nobody what
 * to ask their admin for, and "the button is gone" is a harder support conversation than
 * "the button says I need media:write".
 */
export function Can({
  perm,
  superAdmin,
  fallback = null,
  children,
}: {
  perm?: string;
  /** Additionally require the platform SuperAdmin role. */
  superAdmin?: boolean;
  fallback?: React.ReactNode;
  children: React.ReactNode;
}) {
  const allowed = useCan(perm);
  const isSuperAdmin = useIsSuperAdmin();
  if (superAdmin && !isSuperAdmin) return <>{fallback}</>;
  return allowed ? <>{children}</> : <>{fallback}</>;
}
