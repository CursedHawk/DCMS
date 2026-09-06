import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { adminApi } from '../../lib/api';

/**
 * admin-api rather than platform-api, and not by accident: admin-api owns and migrates the
 * `edge` schema these rows live in. See ADR 0011.
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
  lastAttemptAt: string | null;
  expired: boolean;
  daysRemaining: number | null;
  /** Counts against Let's Encrypt's five-per-week duplicate-certificate limit. */
  issuedThisWeek: number;
}

export interface CertificateAttempt {
  attemptedAt: string;
  succeeded: boolean;
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
    queryFn: () => adminApi.get<ManagedCertificate[]>('/admin/platform/certificates'),
    // An order takes a DNS propagation wait plus a CA validation, so the interesting state
    // changes on the order of a minute, not a second.
    refetchInterval: 30_000,
  });
}

export function useCertificateAttempts(id: string | null) {
  return useQuery({
    queryKey: [...KEY, 'attempts', id],
    enabled: id !== null,
    queryFn: () => adminApi.get<CertificateAttempt[]>(`/admin/platform/certificates/${id}/attempts`),
  });
}

export function useCreateCertificate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: CertificateInput) =>
      adminApi.post<ManagedCertificate>('/admin/platform/certificates', body),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useUpdateCertificate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, ...body }: CertificateInput & { id: string }) =>
      adminApi.put<ManagedCertificate>(`/admin/platform/certificates/${id}`, body),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useDeleteCertificate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => adminApi.del<void>(`/admin/platform/certificates/${id}`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useReissueCertificate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      adminApi.post<void>(`/admin/platform/certificates/${id}/reissue`, {}),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}
