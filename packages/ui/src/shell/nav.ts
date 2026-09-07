import type { LucideIcon } from 'lucide-react';

/**
 * One destination in the sidebar.
 *
 * Deliberately post-translation and post-permission: the app resolves `label` through its own
 * i18next instance and filters the list against the caller's permissions before handing it
 * over. `@dcms/ui` renders navigation; it does not decide what a person may see, and it does
 * not own either app's locale bundle.
 */
export interface ShellNavItem {
  to: string;
  label: string;
  icon: LucideIcon;
  /** Which `ShellNavGroup` this belongs under. Ungrouped items render first. */
  group?: string;
  /**
   * Match the path exactly instead of by prefix. Needed for '/', which is a prefix of
   * everything and would otherwise light up on every page.
   */
  exact?: boolean;
  /** A count to show beside the label — unread submissions, failing builds. */
  badge?: number;
}

export interface ShellNavGroup {
  id: string;
  /** Omitted for a group that is a visual break rather than a named section. */
  label?: string;
}

/** Whether a nav item is the one the current path belongs to. */
export function isActive(item: ShellNavItem, pathname: string): boolean {
  if (item.exact || item.to === '/') return pathname === item.to;
  return pathname === item.to || pathname.startsWith(`${item.to}/`);
}

/**
 * The item that best matches the current path.
 *
 * Longest match wins, so `/settings/members` highlights Members rather than Settings when both
 * are in the nav. Returns undefined on a path no item covers, which is a real case: the OIDC
 * callback and the invite-accept page live outside the navigation.
 */
export function activeItem(
  items: readonly ShellNavItem[],
  pathname: string,
): ShellNavItem | undefined {
  return items
    .filter((i) => isActive(i, pathname))
    .sort((a, b) => b.to.length - a.to.length)[0];
}
