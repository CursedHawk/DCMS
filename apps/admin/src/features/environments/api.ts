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
