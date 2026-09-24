import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { platformApi } from '../../lib/api';

/**
 * Served by platform-api, performed by admin-api, enforced by the edge — the same path as the
 * certificates page, for the same reason: the rows live in the `edge` schema, which admin-api
 * owns. The edge reloads the list as soon as a change is saved; no restart is involved.
 */
export interface RateLimitExemption {
  id: string;
  /** Canonical CIDR: a single address comes back as /32 or /128. */
  cidr: string;
  note: string;
  createdAt: string;
  createdBy: string | null;
}

export interface RateLimitExemptionInput {
  /** An address or a CIDR range, as typed; the server normalises it. */
  address: string;
  note: string;
}

const KEY = ['platform-rate-limit-exemptions'];

export function useRateLimitExemptions() {
  return useQuery({
    queryKey: KEY,
    queryFn: () => platformApi.get<RateLimitExemption[]>('/rate-limit-exemptions'),
  });
}

export function useAddRateLimitExemption() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: RateLimitExemptionInput) =>
      platformApi.post<RateLimitExemption>('/rate-limit-exemptions', body),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useRemoveRateLimitExemption() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => platformApi.del<void>(`/rate-limit-exemptions/${id}`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}
