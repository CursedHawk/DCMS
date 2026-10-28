import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../lib/api';
import { runtimeConfig } from '../../runtime-config';
import { adminHeaders } from '../../tenants';

/** One environment of the workspace (ADR 0028): a dimension of the tenant, production's id the tenant's. */
export interface Environment {
  id: string;
  name: string;
  order: number;
  purpose: 'stage' | 'feature' | 'service';
  isProduction: boolean;
  /** The environments allowed to deploy configuration into this one. */
  promotesFrom: string[];
  /** Whether the caller holds a role there, and so can open it. */
  isMember: boolean;
}

export interface Environments {
  /** More than production. */
  enabled: boolean;
  workspaceId: string;
  environments: Environment[];
}

/**
 * The workspace's environments, asked for without the environment header: the list is how a
 * member finds one they can open, so it has to answer when the selected one is gone or closed to them.
 */
export function useEnvironments() {
  return useQuery({
    queryKey: ['environments'],
    queryFn: async () => {
      const { 'X-Dcms-Environment': _, ...headers } = await adminHeaders();
      const res = await fetch(`${runtimeConfig.adminApiBase}/admin/environments`, { headers });
      if (!res.ok) throw new Error(`environments failed: ${res.status}`);
      return (await res.json()) as Environments;
    },
  });
}

/** Every change here also changes what the switchers list. */
function useEnvironmentMutation<TInput>(fn: (input: TInput) => Promise<unknown>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['environments'] });
      void qc.invalidateQueries({ queryKey: ['me-tenants'] });
    },
  });
}

/** A new environment: a copy of `from`'s configuration (production when absent), and of the `copy` data scopes too. */
export const useAddEnvironment = () =>
  useEnvironmentMutation((body: { name: string; from?: string; copy?: string[] }) =>
    api.post<Environment>('/admin/environments', body),
  );

/** What duplicating can copy besides configuration (ADR 0028). */
export interface CopyScope {
  scope: string;
  /** About people: offered with a warning. */
  personalData: boolean;
}

export const useCopyScopes = () =>
  useQuery({ queryKey: ['environments', 'copy-scopes'], queryFn: () => api.get<CopyScope[]>('/admin/environments/copy-scopes') });
export const useUpdateEnvironment = () =>
  useEnvironmentMutation(
    ({ id, ...body }: { id: string; name?: string; order?: number; promotesFrom?: string[] }) =>
      api.patch<Environment>(`/admin/environments/${id}`, body),
  );
export const useDeleteEnvironment = () =>
  useEnvironmentMutation((id: string) => api.del(`/admin/environments/${id}`));

/** One configuration change of a migration, with how it lands in this environment when reviewed. */
export interface ConfigChange {
  id: string;
  kind: string;
  key: string;
  label: string;
  op: 'add' | 'update' | 'delete';
  before: unknown;
  after: unknown;
  current?: unknown;
  outcome?: 'apply' | 'already' | 'merge' | 'conflict' | 'local' | null;
  /** What applying writes when the change merges into what this environment changed itself. */
  merged?: unknown;
  /** Where both sides changed the same thing differently; an empty path is the whole item. */
  conflicts?: MergeConflict[] | null;
}

export interface MergeConflict {
  path: string;
  ours: unknown;
  theirs: unknown;
}

export interface Migration {
  migrationId: string;
  name: string;
  description: string | null;
  origin: string;
  originName: string | null;
  authorEmail: string | null;
  createdAt: string;
  appliedAt: string;
  rolledBackAt: string | null;
  resolutions: Record<string, string>;
  changes: ConfigChange[];
}

export interface Reviewed {
  migration: Migration;
  changes: ConfigChange[];
}

/**
 * Keyed `{migrationId}/{changeId}` for the whole change (overwrite or skip), or
 * `{migrationId}/{changeId}#{path}` for one conflicting path (ours or theirs): one item can change
 * in several pending migrations.
 */
export type Resolutions = Record<string, 'overwrite' | 'skip' | 'ours' | 'theirs'>;

/** Whether a reviewed change can go ahead with these choices. */
export function isResolved(migrationId: string, change: ConfigChange, resolutions: Resolutions): boolean {
  if (change.outcome !== 'conflict') return true;
  if (resolutions[`${migrationId}/${change.id}`]) return true;
  const conflicts = change.conflicts ?? [];
  return (
    conflicts.length > 0 &&
    conflicts.every((c) => c.path !== '' && resolutions[`${migrationId}/${change.id}#${c.path}`])
  );
}

export const useUnpublished = (enabled: boolean) =>
  useQuery({
    queryKey: ['environments', 'changes'],
    enabled,
    queryFn: () => api.get<ConfigChange[]>('/admin/environments/changes'),
  });

export const useHistory = () =>
  useQuery({
    queryKey: ['environments', 'migrations'],
    queryFn: () => api.get<Migration[]>('/admin/environments/migrations'),
  });

export const usePending = (from: string, enabled: boolean) =>
  useQuery({
    queryKey: ['environments', 'pending', from],
    enabled,
    queryFn: () => api.get<Reviewed[]>(`/admin/environments/pending?from=${from}`),
  });

export const useRollbackPlan = (migrationId: string | null) =>
  useQuery({
    queryKey: ['environments', 'rollback', migrationId],
    enabled: !!migrationId,
    queryFn: () => api.get<Reviewed>(`/admin/environments/migrations/${migrationId}/rollback`),
  });

export const useCutMigration = () =>
  useEnvironmentMutation((body: { name: string; description?: string; changes?: string[] }) =>
    api.post<Migration>('/admin/environments/migrations', body),
  );

export const useApplyPending = () =>
  useEnvironmentMutation((body: { from: string; upTo: string; resolutions: Resolutions }) =>
    api.post<{ jobId: string }>('/admin/environments/pending/apply', body),
  );

export const useRollback = () =>
  useEnvironmentMutation(({ migrationId, resolutions }: { migrationId: string; resolutions: Resolutions }) =>
    api.post<{ jobId: string }>(`/admin/environments/migrations/${migrationId}/rollback`, { resolutions }),
  );

export type Ownership = 'Mixed' | 'Local' | 'Managed';

export interface Policy {
  kind: string;
  itemKey: string | null;
  mode: Ownership;
}

/** The kinds of configuration that move between environments, in the order a deployment applies them. */
export const CONFIG_KINDS = ['roles', 'plugins', 'api-connections', 'sites', 'user-auth', 'dynamic-apps'] as const;

/** Every kind a policy can name: configuration, and published content (ADR 0028), which a deployment carries only when ticked. */
export const POLICY_KINDS = [...CONFIG_KINDS, 'content'] as const;

export const usePolicies = (environmentId: string) =>
  useQuery({
    queryKey: ['environments', 'policies', environmentId],
    queryFn: () => api.get<Policy[]>(`/admin/environments/${environmentId}/policies`),
  });

export function useSavePolicies(environmentId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (policies: Policy[]) => api.put(`/admin/environments/${environmentId}/policies`, policies),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['environments', 'policies', environmentId] }),
  });
}

export interface EnvironmentLink {
  id: string;
  kind: 'app' | 'users';
  targetEnvironmentId: string;
  targetKey: string | null;
  access: 'Read' | 'ReadWrite';
}

export const useLinks = (environmentId: string) =>
  useQuery({
    queryKey: ['environments', 'links', environmentId],
    queryFn: () => api.get<EnvironmentLink[]>(`/admin/environments/${environmentId}/links`),
  });

export function useLinkMutations(environmentId: string) {
  const qc = useQueryClient();
  const onSuccess = () => void qc.invalidateQueries({ queryKey: ['environments', 'links', environmentId] });
  return {
    add: useMutation({
      mutationFn: (
        body:
          | { kind: 'app'; targetEnvironmentId: string; targetKey: string; access: 'read' | 'readWrite' }
          | { kind: 'users'; targetEnvironmentId: string },
      ) => api.post(`/admin/environments/${environmentId}/links`, body),
      onSuccess,
    }),
    remove: useMutation({
      mutationFn: (linkId: string) => api.del(`/admin/environments/${environmentId}/links/${linkId}`),
      onSuccess,
    }),
  };
}
