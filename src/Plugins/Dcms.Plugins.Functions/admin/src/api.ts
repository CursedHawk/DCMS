import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { instancePath, usePluginApi } from '@dcms/plugin-ui';

// The shapes Dcms.Plugins.Functions sends (Deploy/DeploymentService.cs, Http/ApiKeys.cs).

export interface RepoInfo {
  id: string;
  repoFullName: string;
  defaultBranch: string;
  httpCloneUrl?: string | null;
  sshCloneUrl?: string | null;
}

export type DeploymentStatus = 'Building' | 'Failed' | 'Ready' | 'Active' | 'Superseded' | 'RolledBack';

export interface DeploymentSummary {
  id: string;
  number: number;
  commitSha: string;
  status: DeploymentStatus;
  error?: string | null;
  functions: string[];
  createdAt: string;
  activatedAt?: string | null;
  activatedBy?: string | null;
}

export interface FunctionSpec {
  name: string;
  description?: string | null;
  triggers: Array<Record<string, unknown>>;
  http: string[];
  uses: { tables: Record<string, string[]>; contracts: string[]; egress: string[]; secrets: string[]; emits: string[] };
  timeoutMs?: number | null;
  heapMb?: number | null;
}

export interface Diagnostic {
  message: string;
  file?: string | null;
  line?: number | null;
  column?: number | null;
}

export interface DeploymentDetail {
  deployment: DeploymentSummary;
  functions: FunctionSpec[];
  diagnostics?: Diagnostic[] | null;
  permissions: string[];
}

export type RunStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Terminated';

export interface RunSummary {
  id: string;
  function: string;
  deployment: number;
  trigger: string;
  status: RunStatus;
  attempts: number;
  error?: string | null;
  durationMs: number;
  createdAt: string;
  finishedAt?: string | null;
}

export interface RunDetail {
  run: RunSummary;
  input?: unknown;
  trigger?: unknown;
  result?: unknown;
  logs?: string | null;
  calls: number;
  correlationId: string;
  depth: number;
}

export interface ApiKeyInfo {
  id: string;
  name: string;
  prefix: string;
  functions: string[];
  createdAt: string;
  createdBy?: string | null;
}

const key = (slug: string, ...rest: unknown[]) => ['functions', slug, ...rest];

export function useRepo(slug: string) {
  const api = usePluginApi();
  return useQuery({ queryKey: key(slug, 'repo'), queryFn: () => api.get<RepoInfo>(instancePath(slug, '/_functions/repo')) });
}

export function useDeployments(slug: string) {
  const api = usePluginApi();
  return useQuery({
    queryKey: key(slug, 'deployments'),
    // A build is a tracked job: the jobs indicator shows it, and the list is read again on focus.
    queryFn: () => api.get<DeploymentSummary[]>(instancePath(slug, '/_functions/deployments')),
  });
}

export function useDeployment(slug: string, id: string | null) {
  const api = usePluginApi();
  return useQuery({
    queryKey: key(slug, 'deployment', id),
    queryFn: () => api.get<DeploymentDetail>(instancePath(slug, `/_functions/deployments/${id}`)),
    enabled: !!id,
  });
}

/** The live deployment's functions: what the app runs now. */
export function useLive(slug: string) {
  const deployments = useDeployments(slug);
  const live = deployments.data?.find((d) => d.status === 'Active') ?? null;
  return { deployments, live, detail: useDeployment(slug, live?.id ?? null) };
}

export function useDeploymentActions(slug: string) {
  const api = usePluginApi();
  const qc = useQueryClient();
  const settle = () => qc.invalidateQueries({ queryKey: key(slug) });
  return {
    activate: useMutation({
      mutationFn: (id: string) => api.post<DeploymentSummary>(instancePath(slug, `/_functions/deployments/${id}/activate`), {}),
      onSuccess: settle,
    }),
    rebuild: useMutation({
      mutationFn: () => api.post<{ jobId: string }>(instancePath(slug, '/_functions/build'), {}),
      onSuccess: settle,
    }),
  };
}

export function useRuns(slug: string, filter: { function?: string; status?: string; page: number }) {
  const api = usePluginApi();
  const qs = new URLSearchParams({ page: String(filter.page) });
  if (filter.function) qs.set('function', filter.function);
  if (filter.status) qs.set('status', filter.status);
  return useQuery({
    queryKey: key(slug, 'runs', filter),
    queryFn: () => api.get<{ items: RunSummary[]; page: number; more: boolean }>(instancePath(slug, `/_functions/runs?${qs}`)),
  });
}

export function useRun(slug: string, id: string | null) {
  const api = usePluginApi();
  return useQuery({
    queryKey: key(slug, 'run', id),
    queryFn: () => api.get<RunDetail>(instancePath(slug, `/_functions/runs/${id}`)),
    enabled: !!id,
  });
}

export function useKeys(slug: string) {
  const api = usePluginApi();
  return useQuery({ queryKey: key(slug, 'keys'), queryFn: () => api.get<ApiKeyInfo[]>(instancePath(slug, '/_functions/keys')) });
}

export function useKeyActions(slug: string) {
  const api = usePluginApi();
  const qc = useQueryClient();
  const settle = () => qc.invalidateQueries({ queryKey: key(slug, 'keys') });
  return {
    create: useMutation({
      mutationFn: (body: { name: string; functions: string[] }) =>
        api.post<{ key: ApiKeyInfo; secret: string }>(instancePath(slug, '/_functions/keys'), body),
      onSuccess: settle,
    }),
    revoke: useMutation({
      mutationFn: (id: string) => api.del(instancePath(slug, `/_functions/keys/${id}`)),
      onSuccess: settle,
    }),
  };
}

export function useSecrets(slug: string) {
  const api = usePluginApi();
  return useQuery({ queryKey: key(slug, 'secrets'), queryFn: () => api.get<string[]>(instancePath(slug, '/_functions/secrets')) });
}

export function useSecretActions(slug: string) {
  const api = usePluginApi();
  const qc = useQueryClient();
  const settle = () => qc.invalidateQueries({ queryKey: key(slug, 'secrets') });
  return {
    set: useMutation({
      mutationFn: ({ name, value }: { name: string; value: string }) =>
        api.put(instancePath(slug, `/_functions/secrets/${encodeURIComponent(name)}`), { value }),
      onSuccess: settle,
    }),
    remove: useMutation({
      mutationFn: (name: string) => api.del(instancePath(slug, `/_functions/secrets/${encodeURIComponent(name)}`)),
      onSuccess: settle,
    }),
  };
}

export function useRunNow(slug: string) {
  const api = usePluginApi();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { function: string; input?: unknown; deployment?: string }) =>
      api.post<{ status: string; result?: unknown; error?: string | null; logs: string; calls: number; durationMs: number }>(
        instancePath(slug, '/_functions/run'), body),
    onSuccess: () => qc.invalidateQueries({ queryKey: key(slug, 'runs') }),
  });
}

/** Where a function answers over HTTP on the tenant's sites. */
export function httpPath(slug: string, name: string) {
  return `/api/${slug}/fn/${name}`;
}
