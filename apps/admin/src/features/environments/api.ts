import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../lib/api';

/** One environment of the workspace (ADR 0026): a whole tenant of its own. */
export interface Environment {
  tenantId: string;
  slug: string;
  name: string;
  order: number;
  isRoot: boolean;
  /** The environments allowed to promote configuration into this one. */
  promotesFrom: string[];
  status: string;
  /** Whether the caller holds a role there, and so can open it. */
  isMember: boolean;
}

export interface Environments {
  enabled: boolean;
  workspaceId: string;
  environments: Environment[];
}

export function useEnvironments() {
  return useQuery({
    queryKey: ['environments'],
    queryFn: () => api.get<Environments>('/admin/environments'),
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

export const useEnableEnvironments = () => useEnvironmentMutation(() => api.post('/admin/environments/enable'));
export const useDisableEnvironments = () => useEnvironmentMutation(() => api.post('/admin/environments/disable'));
export const useAddEnvironment = () =>
  useEnvironmentMutation((name: string) => api.post<Environment>('/admin/environments', { name }));
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
  outcome?: 'apply' | 'already' | 'conflict' | 'local' | null;
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

/** Keyed `{migrationId}/{changeId}`: one item can change in several pending migrations. */
export type Resolutions = Record<string, 'overwrite' | 'skip'>;

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
  useEnvironmentMutation((body: { name: string; description?: string }) =>
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
