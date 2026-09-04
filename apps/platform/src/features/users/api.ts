import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { identityApi } from '../../lib/api';

export interface UserRow {
  id: string;
  email: string | null;
  displayName: string | null;
  emailConfirmed: boolean;
  createdAt: string;
  lockedOut: boolean;
  lockoutEnd: string | null;
  accessFailedCount: number;
  forgejoUsername: string | null;
  hasGitPassword: boolean;
  roles: string[];
}

export interface UserPage {
  total: number;
  page: number;
  pageSize: number;
  items: UserRow[];
}

export function useUsers(search: string, lockedOnly: boolean) {
  const params = new URLSearchParams();
  if (search) params.set('search', search);
  if (lockedOnly) params.set('lockedOut', 'true');
  const qs = params.toString();

  return useQuery({
    queryKey: ['platform-users', search, lockedOnly],
    queryFn: () => identityApi.get<UserPage>(`/users${qs ? `?${qs}` : ''}`),
  });
}

/** Every mutation invalidates the list, because every one of them changes a column it shows. */
function useUserMutation<T>(fn: (input: T) => Promise<unknown>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['platform-users'] }),
  });
}

export const useSetLock = () =>
  useUserMutation(({ id, locked }: { id: string; locked: boolean }) =>
    identityApi.post(`/users/${id}/${locked ? 'lock' : 'unlock'}`));

export const useGrantRole = () =>
  useUserMutation(({ id, role }: { id: string; role: string }) =>
    identityApi.post(`/users/${id}/roles`, { role }));

export const useRevokeRole = () =>
  useUserMutation(({ id, role }: { id: string; role: string }) =>
    identityApi.del(`/users/${id}/roles/${encodeURIComponent(role)}`));

export const useConfirmEmail = () =>
  useUserMutation(({ id }: { id: string }) => identityApi.post(`/users/${id}/confirm-email`));
