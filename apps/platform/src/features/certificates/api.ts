import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { platformApi } from '../../lib/api';

/**
 * Served by platform-api, performed by admin-api.
 *
 * These rows live in the `edge` schema, which admin-api owns and migrates and which the
 * console's least-privilege database role holds no grant on (ADR 0011). So platform-api checks
 * the operator against platform:certificates:manage and forwards; the shapes below are
 * admin-api's, relayed verbatim.
 */
export interface ManagedCertificate {
  id: string;
  name: string;
  identifiers: string[];
  enabled: boolean;
  /** True when any identifier is a wildcard, which forces DNS-01 and so needs a DNS token. */
  requiresDns: boolean;
  issuer: string | null;
  notBefore: string | null;
  notAfter: string | null;
  renewedAt: string | null;
  /** What the issued certificate actually covers, read off its SAN extension. */
  covers: string[];
  reissueRequested: boolean;
  lastError: string | null;
  /** False when the last failure never became an order — a missing token, not the CA refusing. */
  lastErrorReachedCa: boolean;
  lastAttemptAt: string | null;
  expired: boolean;
  daysRemaining: number | null;
  /** Counts against Let's Encrypt's five-per-week duplicate-certificate limit. */
  issuedThisWeek: number;
}

export interface CertificateAttempt {
  attemptedAt: string;
  succeeded: boolean;
  /** Whether the CA was actually asked. False attempts cost no rate limit. */
  reachedCa: boolean;
  error: string | null;
  identifiers: string;
}

export interface CertificateInput {
  name: string;
  identifiers: string[];
  enabled: boolean;
}

const KEY = ['platform-managed-certificates'];

export function useManagedCertificates() {
  return useQuery({
    queryKey: KEY,
    queryFn: () => platformApi.get<ManagedCertificate[]>('/certificates'),
    // A fallback now rather than the mechanism: every outcome that changes this table also
    // raises a platform notification, and that push invalidates this query (see
    // `useConsoleHub`). The interval is what covers a dropped socket, and it is set to the
    // period of the worker that notices an outcome in the first place — polling faster than
    // the thing being polled can learn anything is asking a question with no new answer.
    refetchInterval: 2 * 60_000,
  });
}

export function useCertificateAttempts(id: string | null) {
  return useQuery({
    queryKey: [...KEY, 'attempts', id],
    enabled: id !== null,
    queryFn: () => platformApi.get<CertificateAttempt[]>(`/certificates/${id}/attempts`),
  });
}

export function useCreateCertificate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: CertificateInput) =>
      platformApi.post<ManagedCertificate>('/certificates', body),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useUpdateCertificate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, ...body }: CertificateInput & { id: string }) =>
      platformApi.put<ManagedCertificate>(`/certificates/${id}`, body),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useDeleteCertificate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => platformApi.del<void>(`/certificates/${id}`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useReissueCertificate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      platformApi.post<void>(`/certificates/${id}/reissue`, {}),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}
