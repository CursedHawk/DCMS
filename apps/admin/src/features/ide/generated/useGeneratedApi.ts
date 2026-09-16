import { useMutation, useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { ideApi } from '../../site-source/ide';
import { useVfs } from '../../site-source/vfs';
import { appendOutput } from '../panel/output';
import { MANIFEST_PATH, planGeneratedUpdate, readManifest, usesGeneratedLayer } from './generatedLayer';

/** The react-query key the notification hub invalidates when the tenant's plugins change. */
export const API_FINGERPRINT_KEY = ['api-fingerprint'] as const;

/**
 * Keeps a site's generated API client in step with the tenant's plugins — by hand (Refresh API)
 * and on its own.
 *
 * <h3>How it notices, without polling</h3>
 * <p>The server answers one cheap question: the fingerprint of what it would generate now. The site
 * records the fingerprint of what it has in `src/api/manifest.json`. Different means stale. The
 * question is asked when the editor opens and again whenever the notification hub says the
 * tenant's plugins changed (`plugins` in `LIVE_QUERY_MAP` invalidates this key) or reconnects —
 * never on a timer.</p>
 *
 * <h3>When it holds off</h3>
 * <ul>
 *   <li><b>During an agent run.</b> The run has a picture of these files and may be editing code that
 *   calls them; rewriting them underneath it turns a coherent change into a confusing one. The
 *   refresh happens when the run ends.</li>
 *   <li><b>While the draft is not settled</b> — loading, switching branch, or in conflict. Writing
 *   into a workspace the author is being asked to reconcile adds to what they must reconcile.</li>
 *   <li><b>On a site that never used the generated client</b> (see `usesGeneratedLayer`).</li>
 *   <li><b>Once per fingerprint per branch.</b> If an attempt fails or lands on a different
 *   fingerprint than expected, it is not retried in a loop; the next change notice tries again.</li>
 * </ul>
 *
 * <p>Writes go through `writeFile`/`deleteFile` like any edit, so they autosave, show in source
 * control, and can be reviewed or reverted like everything else.</p>
 */
export function useGeneratedApi({ siteId, settled }: { siteId: string; settled: boolean }) {
  const { t } = useTranslation();

  const branch = useVfs((s) => s.branch);
  const manifestRaw = useVfs((s) => s.files[MANIFEST_PATH]);
  const eligible = useVfs((s) => usesGeneratedLayer(s.files));
  const agentRunning = useVfs((s) => s.agentRuns > 0);
  const siteFingerprint = useMemo(() => readManifest(manifestRaw)?.fingerprint ?? null, [manifestRaw]);

  const server = useQuery({
    queryKey: API_FINGERPRINT_KEY,
    queryFn: ideApi.apiFingerprint,
    enabled: settled && eligible,
    // Pushed, not polled: the hub invalidates this key when plugins change.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });

  const refresh = useMutation({
    mutationFn: (_trigger: 'manual' | 'auto') => ideApi.generatedFiles(siteId),
    onSuccess: ({ files }, trigger) => {
      const vfs = useVfs.getState();
      const { writes, deletes } = planGeneratedUpdate(vfs.files, files);
      for (const [path, content] of writes) vfs.writeFile(path, content);
      for (const path of deletes) vfs.deleteFile(path);

      const count = writes.length + deletes.length;
      const message =
        count === 0
          ? t('ide.generatedUpToDate')
          : trigger === 'auto'
            ? t('ide.generatedAutoRefreshed', { count })
            : t('ide.generatedRefreshed', { count });
      // A manual press always answers; an automatic refresh interrupts only when it changed
      // something, and then says why, because files changing on their own need a reason.
      if (trigger === 'manual' || count > 0) toast.success(message);
      appendOutput('workspace', message);
      for (const [path] of writes) appendOutput('workspace', `regenerated ${path}`);
      for (const path of deletes) appendOutput('workspace', `removed ${path} (no longer generated)`);
    },
    onError: (_error, trigger) => {
      if (trigger === 'manual') toast.error(t('errors.generic'));
      appendOutput('workspace', t('ide.generatedRefreshFailed'), 'error');
    },
  });

  const attempted = useRef<string | null>(null);
  const target = server.data?.fingerprint;
  const { mutate, isPending } = refresh;

  useEffect(() => {
    if (!settled || !eligible || agentRunning || isPending || !target) return;
    if (siteFingerprint === target) return;
    const key = `${branch}:${target}`;
    if (attempted.current === key) return;
    attempted.current = key;
    mutate('auto');
  }, [settled, eligible, agentRunning, isPending, target, siteFingerprint, branch, mutate]);

  return {
    refresh: () => mutate('manual'),
    refreshing: isPending,
    /** True when the site's client is known to be behind the tenant's plugins. */
    stale: eligible && !!target && siteFingerprint !== target,
  };
}
