/**
 * The shape every DCMS permission endpoint returns: whether the caller is a platform
 * SuperAdmin, and the permission keys they hold. `GET /api/admin/me/permissions` (tenant
 * scope) and `GET /api/platform/me` (platform scope) both answer with it.
 */
export interface MyPermissions {
  isSuperAdmin: boolean;
  permissions: string[];
}

/**
 * SuperAdmin short-circuits, mirroring the server's own
 * `PermissionAuthorizationHandler` — the two must agree or the UI offers buttons the
 * API refuses. This is presentation only: the server decides.
 */
export function can(me: MyPermissions | undefined, permission: string): boolean {
  if (!me) return false;
  return me.isSuperAdmin || me.permissions.includes(permission);
}
