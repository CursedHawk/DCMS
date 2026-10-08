import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toastApiError } from '@dcms/ui';
import { instancePath, usePluginApi, usePluginScreen, usePluginT } from '@dcms/plugin-ui';

// The plugin's console routes (UserAuthAdminEndpoints) and identity's realm records, as sent.

export interface RealmInfo {
  tenantId: string;
  slug: string;
  name: string;
  hosts: string[];
  clientId: string;
  /** False until identity holds the edge's secret: no site can sign anyone in yet. */
  clientReady: boolean;
  passwordEnabled: boolean;
}

export type UserStatus = 'invited' | 'active' | 'disabled';

export interface SiteUser {
  id: string;
  email: string;
  displayName: string | null;
  status: UserStatus;
  groups: string[];
  hasPassword: boolean;
  lockedOut: boolean;
  createdAt: string;
  lastSignInAt: string | null;
}

export interface UserPage {
  items: SiteUser[];
  total: number;
  page: number;
  pageSize: number;
}

export interface Group {
  id: string;
  name: string;
  description: string | null;
  members: number;
}

export type ProviderKind = 'google' | 'entra' | 'oidc' | 'dcms';

export interface Provider {
  key: string;
  kind: ProviderKind;
  displayName: string;
  enabled: boolean;
  clientId: string | null;
  hasSecret: boolean;
  issuer: string | null;
  entraTenant: string | null;
  hostedDomain: string | null;
  /** inviteOnly | allowedDomains */
  provisioning: string;
  allowedDomains: string[];
  defaultGroups: string[];
  groupClaim: string | null;
  groupMappings: Record<string, string>;
  /** What to register at the provider as the redirect URI; null for DCMS. */
  callbackUrl: string | null;
}

export interface ProviderWrite {
  kind: ProviderKind;
  displayName: string;
  enabled: boolean;
  clientId?: string | null;
  /** Write-only: omit to keep the stored one. */
  clientSecret?: string | null;
  issuer?: string | null;
  entraTenant?: string | null;
  hostedDomain?: string | null;
  provisioning?: string;
  allowedDomains?: string[];
  defaultGroups?: string[];
}

export interface Grant {
  id: string;
  subjectType: 'group' | 'user';
  subjectId: string;
}

export interface Role {
  id: string;
  key: string;
  name: string;
  description: string | null;
  permissions: string[];
  grants: Grant[];
}

export interface ResourceCatalog {
  plugin: string;
  instance: string;
  label: string;
  resources: { resource: string; label: string; actions: { action: string; label: string }[] }[];
}

export type Access = 'public' | 'signedIn' | 'groups';

export interface Rule {
  prefix: string;
  access: Access;
  groups: string[];
}

export interface SiteWithRules {
  id: string;
  name: string;
  /** A single-page app (React): its route changes never reach the edge, so rules guard direct loads only. */
  spa: boolean;
  hosts: { hostname: string; verified: boolean }[];
  rules: Rule[];
}

export type ApiAccess = 'public' | 'signedIn' | 'permission';

/** Who may call one plugin instance's site API, /api/{slug}. */
export interface InstanceApiAccess {
  instanceId: string;
  slug: string;
  name: string;
  plugin: string;
  access: ApiAccess;
  readPermission: string;
  writePermission: string;
}

/** `/admin/plugins/{slug}{path}` of the instance this screen belongs to. */
export function usePath() {
  const { instance } = usePluginScreen();
  return (path: string) => instancePath(instance?.slug ?? 'users', path);
}

export function useRealm() {
  const api = usePluginApi();
  const path = usePath();
  return useQuery({ queryKey: ['user-auth', 'realm'], queryFn: () => api.get<RealmInfo>(path('/realm')) });
}

export function useUsers(search: string, page: number) {
  const api = usePluginApi();
  const path = usePath();
  const q = new URLSearchParams({ page: String(page), pageSize: '50' });
  if (search.trim()) q.set('search', search.trim());
  return useQuery({
    queryKey: ['user-auth', 'users', search.trim(), page],
    queryFn: () => api.get<UserPage>(path(`/users?${q}`)),
    placeholderData: (previous) => previous,
  });
}

export function useGroups() {
  const api = usePluginApi();
  const path = usePath();
  return useQuery({ queryKey: ['user-auth', 'groups'], queryFn: () => api.get<Group[]>(path('/groups')) });
}

export function useProviders() {
  const api = usePluginApi();
  const path = usePath();
  return useQuery({ queryKey: ['user-auth', 'providers'], queryFn: () => api.get<Provider[]>(path('/providers')) });
}

export function useRoles() {
  const api = usePluginApi();
  const path = usePath();
  return useQuery({ queryKey: ['user-auth', 'roles'], queryFn: () => api.get<Role[]>(path('/roles')) });
}

export function useResources() {
  const api = usePluginApi();
  const path = usePath();
  return useQuery({ queryKey: ['user-auth', 'resources'], queryFn: () => api.get<ResourceCatalog[]>(path('/resources')) });
}

export function useSites() {
  const api = usePluginApi();
  const path = usePath();
  return useQuery({ queryKey: ['user-auth', 'sites'], queryFn: () => api.get<SiteWithRules[]>(path('/sites')) });
}

export function useApiAccess() {
  const api = usePluginApi();
  const path = usePath();
  return useQuery({ queryKey: ['user-auth', 'api-access'], queryFn: () => api.get<InstanceApiAccess[]>(path('/api-access')) });
}

/**
 * A write against the plugin's routes that refreshes everything the plugin shows afterwards
 * (the screens are small; precise invalidation is not worth its bugs) and reports a failure
 * with the server's own message.
 */
export function useWrite<TArgs>(run: (args: TArgs) => Promise<unknown>, onDone?: () => void) {
  const qc = useQueryClient();
  const { t } = usePluginT();
  return useMutation({
    mutationFn: run,
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['user-auth'] });
      onDone?.();
    },
    onError: (error) => toastApiError(error, t),
  });
}

export function displayName(user: Pick<SiteUser, 'displayName' | 'email'>) {
  return user.displayName?.trim() || user.email;
}
