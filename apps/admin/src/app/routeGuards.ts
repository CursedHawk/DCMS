import { Perm } from '../lib/permissions';

export interface RouteGuard {
  /** The permission key the page's endpoints are gated on. */
  perm?: string;
  /** Additionally require the platform SuperAdmin role. */
  superAdmin?: boolean;
}

/**
 * One page inside Settings.
 *
 * <p>These used to be six top-level destinations — Members, Roles, Audit log, Domains, AI,
 * Workspace — plus API Docs, each with its own line in the sidebar. That is a menu that grows by
 * one every time the product gains a setting, and it puts "who can publish" and "what this
 * workspace is called" at the same level as "Content" and "Media", which are the things people
 * actually come here to do.</p>
 *
 * <p>The list is the single source for three things that must agree: the sub-navigation, the
 * router's guards, and where the old URLs redirect to. Declaring them once is what stops a
 * section appearing in the sub-nav and then refusing, or being reachable with no way to find
 * it.</p>
 */
export interface SettingsSection {
  to: string;
  labelKey: string;
  /** Omitted for a section any signed-in member may open. */
  perm?: string;
  /** One line under the heading, so the sub-nav says what each section is for. */
  descriptionKey: string;
}

export const SETTINGS_SECTIONS: readonly SettingsSection[] = [
  {
    to: '/settings/general',
    labelKey: 'settings.general',
    descriptionKey: 'settings.generalHint',
    perm: Perm.TenantSettings,
  },
  {
    to: '/settings/members',
    labelKey: 'nav.members',
    descriptionKey: 'settings.membersHint',
    perm: Perm.MembersManage,
  },
  {
    to: '/settings/roles',
    labelKey: 'nav.roles',
    descriptionKey: 'settings.rolesHint',
    perm: Perm.RolesManage,
  },
  {
    to: '/settings/domains',
    labelKey: 'nav.domains',
    descriptionKey: 'settings.domainsHint',
    perm: Perm.DomainsManage,
  },
  {
    to: '/settings/ai',
    labelKey: 'nav.ai',
    descriptionKey: 'settings.aiHint',
    perm: Perm.AiSettings,
  },
  {
    to: '/settings/audit',
    labelKey: 'nav.audit',
    descriptionKey: 'settings.auditHint',
    perm: Perm.AuditRead,
  },
  // No permission: the API reference describes the endpoints this workspace exposes, and every
  // member can already call them. It is also why /settings is never empty for anybody.
  { to: '/settings/api', labelKey: 'nav.openapi', descriptionKey: 'settings.apiHint' },
];

/**
 * Where the old top-level URLs now live.
 *
 * <p>Not optional politeness. Notification rows written before this change carry `linkPath`
 * values like `/members` and live in the database for the length of the retention window; the
 * platform console deep-links into this app; and people bookmark. Every one of those has to keep
 * working, so each old path stays a real route that redirects rather than a 404 that reads as
 * "the feature was removed".</p>
 */
export const LEGACY_SETTINGS_PATHS: Readonly<Record<string, string>> = {
  '/workspace': '/settings/general',
  '/members': '/settings/members',
  '/roles': '/settings/roles',
  '/domains': '/settings/domains',
  '/ai': '/settings/ai',
  '/audit': '/settings/audit',
  '/openapi': '/settings/api',
};

/**
 * What each page requires, as data.
 *
 * Separate from `routes.tsx` so that it can be checked against `nav.tsx` — the sidebar filters
 * on one set of keys and the router guards on another, and the two silently disagreeing gives
 * you either a nav entry that leads to a refusal, or a page with no link that anyone holding
 * the right permission can still never find. `routeGuards.test.ts` fails the build when they
 * diverge.
 *
 * A path absent from this map is deliberately open to any signed-in member: the dashboard,
 * the account page, the notification list and the invitation-accept page, none of which have a
 * permission that would mean anything.
 */
export const ROUTE_GUARDS: Readonly<Record<string, RouteGuard>> = {
  '/tenants': { superAdmin: true },
  '/plugins': { perm: Perm.PluginsManage },
  '/marketplace': { perm: Perm.PluginsManage },
  '/content': { perm: Perm.ContentRead },
  '/media': { perm: Perm.MediaRead },
  '/forms': { perm: Perm.ContentRead },
  '/sites': { perm: Perm.SiteEdit },
  '/sites/$siteId': { perm: Perm.SiteEdit },
  '/analytics': { perm: Perm.AnalyticsRead },
  '/chat': { perm: Perm.ChatRead },

  // Derived, so a section cannot be listed in the sub-nav and left unguarded in the router.
  ...Object.fromEntries(
    SETTINGS_SECTIONS.filter((s) => s.perm).map((s) => [s.to, { perm: s.perm } as RouteGuard]),
  ),
};

/** Paths reachable by any signed-in member, stated rather than merely omitted. */
export const OPEN_ROUTES: readonly string[] = [
  '/',
  // The landing page redirects to the first section the caller may open, so guarding it on any
  // one permission would refuse people who hold a different one.
  '/settings',
  '/settings/api',
  '/account',
  '/notifications',
  '/invite/accept',
];
