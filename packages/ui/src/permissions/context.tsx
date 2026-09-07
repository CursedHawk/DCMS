import { createContext, useContext } from 'react';
import { type MyPermissions, can } from '@dcms/core';

/**
 * What the signed-in caller may do, made ambient.
 *
 * Both consoles resolve this once in their shell — `GET /api/admin/me/permissions` and
 * `GET /api/platform/me` return the same shape — and every guard below reads it from here
 * rather than threading `me` through a dozen component layers.
 *
 * `undefined` means "not answered yet", and every guard treats it as *deny*. Defaulting open
 * would flash controls that then vanish, which reads as a bug and briefly invites a 403.
 */
const PermissionContext = createContext<MyPermissions | undefined>(undefined);

export function PermissionProvider({
  value,
  children,
}: {
  value: MyPermissions | undefined;
  children: React.ReactNode;
}) {
  return <PermissionContext.Provider value={value}>{children}</PermissionContext.Provider>;
}

export function usePermissions(): MyPermissions | undefined {
  return useContext(PermissionContext);
}

/**
 * Whether the caller holds a permission.
 *
 * Presentation only. The server decides — this exists so the UI does not offer a button the
 * API will refuse, and it mirrors `PermissionAuthorizationHandler`'s SuperAdmin short-circuit
 * so that the two agree.
 */
export function useCan(permission: string | undefined): boolean {
  const me = usePermissions();
  if (!permission) return true;
  return can(me, permission);
}

/** True once the caller is known to be a platform SuperAdmin. */
export function useIsSuperAdmin(): boolean {
  return usePermissions()?.isSuperAdmin ?? false;
}
