import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useRef } from 'react';
import { ideApi, useVfs } from '../site-source';
import { planGeneratedUpdate, readManifest } from '../ide/generated/generatedLayer';

export const RUNTIME_MANIFEST = 'src/dcms/runtime/manifest.json';

/**
 * Keeps a Mode D site's copy of the component runtime (`src/dcms/runtime/`) on the version the
 * canvas draws with.
 *
 * The canvas renders with the runtime built into this admin; the published site builds with the
 * copy in its repository. When they differ, what the author sees is not what ships — so on open,
 * a site whose runtime manifest names another fingerprint gets the current files, through the
 * working draft like any edit (it autosaves, it shows up in source control). Only files the
 * runtime's own manifest lists are ever deleted.
 */
export function useSiteRuntime({ settled }: { settled: boolean }): void {
  const branch = useVfs((s) => s.branch);
  const manifestRaw = useVfs((s) => s.files[RUNTIME_MANIFEST]);
  const agentRunning = useVfs((s) => s.agentRuns > 0);
  const siteFingerprint = useMemo(() => readManifest(manifestRaw)?.fingerprint ?? null, [manifestRaw]);

  const server = useQuery({
    queryKey: ['site-runtime'],
    queryFn: ideApi.siteRuntime,
    enabled: settled,
    // It changes only with a deploy, and a deploy reloads the admin.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });

  const applied = useRef<string | null>(null);
  useEffect(() => {
    const target = server.data;
    if (!settled || agentRunning || !target || siteFingerprint === target.fingerprint) return;
    const key = `${branch}:${target.fingerprint}`;
    if (applied.current === key) return;
    applied.current = key;

    const vfs = useVfs.getState();
    const { writes, deletes } = planGeneratedUpdate(vfs.files, target.files, RUNTIME_MANIFEST);
    for (const [path, content] of writes) vfs.writeFile(path, content);
    for (const path of deletes) vfs.deleteFile(path);
  }, [settled, agentRunning, server.data, siteFingerprint, branch]);
}
