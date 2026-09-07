import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { CheckCircle2, ChevronDown, ChevronRight, CircleDot, Loader2, RotateCw, XCircle } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button, cn } from '@dcms/ui';
import type { GitBuild, GitBuildStatus } from './git';
import { EXPECT_BUILD_WINDOW_MS, expectBuild, expectingBuildSince, gitApi } from './git';
import { useRelativeTime } from './useRelativeTime';

// The Deployments view: every build of the site's `release` branch, newest first,
// with its live status and — when it failed — the full build log inline, so a broken
// publish is diagnosable without leaving the IDE. A "Redeploy" action force-rebuilds
// the current release head (recovers a failed or wedged build).
export function DeploymentsView({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const relative = useRelativeTime();
  const queryClient = useQueryClient();

  const builds = useQuery({
    queryKey: ['site-builds', siteId],
    queryFn: () => gitApi.builds(siteId),
    refetchInterval: (q) => {
      const list = q.state.data ?? [];
      if (list.some((b) => b.status === 'Queued' || b.status === 'Building')) return 2500;

      // Also poll for a while after a publish, even though nothing is in flight YET.
      //
      // This is the gap that made a publish look like it did nothing. Publishing from a
      // feature branch merges into `release`, and the build is created afterwards by the push
      // webhook — so at the moment the panel refreshed, the list genuinely contained no
      // running build. The old condition read that as "idle", stopped polling, and the panel
      // sat on the previous deployment while the real one ran to completion behind it.
      //
      // The hub push is what normally closes this window; this is the fallback for when the
      // socket is unavailable, and it stops on its own the moment a build shows up (the branch
      // above takes over) or the window expires.
      return Date.now() - expectingBuildSince(siteId) < EXPECT_BUILD_WINDOW_MS ? 2000 : false;
    },
  });

  const rebuild = useMutation({
    mutationFn: () => gitApi.rebuild(siteId),
    onSuccess: () => {
      toast.success(t('ide.git.rebuildQueued'));
      expectBuild(siteId);
      queryClient.invalidateQueries({ queryKey: ['site-builds', siteId] });
    },
    onError: () => toast.error(t('errors.generic')),
  });

  const list = builds.data ?? [];

  return (
    <div className="flex h-full flex-col overflow-auto">
      <div className="flex shrink-0 items-center gap-2 border-b p-2">
        <span className="flex-1 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
          {t('ide.deploy.title')}
        </span>
        <Button size="sm" variant="outline" onClick={() => rebuild.mutate()} disabled={rebuild.isPending}>
          <RotateCw className={cn('h-3.5 w-3.5', rebuild.isPending && 'animate-spin')} />
          {t('ide.deploy.redeploy')}
        </Button>
      </div>

      {builds.isLoading && <p className="p-3 text-xs text-muted-foreground">{t('common.loading')}</p>}
      {!builds.isLoading && list.length === 0 && (
        <p className="p-3 text-xs text-muted-foreground">{t('ide.deploy.empty')}</p>
      )}

      <ul className="min-h-0">
        {list.map((b) => (
          <BuildRow key={b.id} siteId={siteId} build={b} relative={relative} />
        ))}
      </ul>
    </div>
  );
}

function BuildRow({
  siteId,
  build,
  relative,
}: {
  siteId: string;
  build: GitBuild;
  relative: (iso?: string | null) => string;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const failed = build.status === 'Failed';

  const log = useQuery({
    queryKey: ['build-log', siteId, build.id],
    queryFn: () => gitApi.buildLog(siteId, build.id),
    enabled: open && build.hasLog,
  });

  return (
    <li className="border-b last:border-b-0">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="flex w-full items-start gap-2 px-2 py-2 text-left hover:bg-accent/40"
      >
        {build.hasLog || build.error ? (
          open ? (
            <ChevronDown className="mt-0.5 h-3 w-3 shrink-0 text-muted-foreground" />
          ) : (
            <ChevronRight className="mt-0.5 h-3 w-3 shrink-0 text-muted-foreground" />
          )
        ) : (
          <span className="w-3 shrink-0" />
        )}
        <StatusIcon status={build.status} />
        <div className="min-w-0 flex-1">
          <p className="flex items-center gap-1.5 text-xs">
            <StatusLabel status={build.status} />
            {build.active && (
              <span className="rounded bg-green-500/15 px-1 py-px text-[9px] font-semibold uppercase tracking-wide text-green-600 dark:text-green-400">
                {t('ide.git.live')}
              </span>
            )}
          </p>
          <p className="mt-0.5 flex items-center gap-1.5 text-[10px] text-muted-foreground">
            {build.shortSha && <code>{build.shortSha}</code>}
            <span title={new Date(build.createdAt).toLocaleString()}>{relative(build.createdAt)}</span>
          </p>
        </div>
      </button>

      {open && (
        <div className="border-t bg-muted/30 px-2 py-2">
          {failed && build.error && (
            <p className="mb-1.5 whitespace-pre-wrap break-words text-[11px] font-medium text-destructive">
              {build.error}
            </p>
          )}
          {build.hasLog ? (
            log.isLoading ? (
              <p className="text-[11px] text-muted-foreground">{t('common.loading')}</p>
            ) : (
              <pre className="max-h-72 overflow-auto rounded border bg-background p-2 text-[10.5px] leading-relaxed text-foreground/90">
                {log.data || t('ide.deploy.noLog')}
              </pre>
            )
          ) : (
            !build.error && <p className="text-[11px] text-muted-foreground">{t('ide.deploy.noLog')}</p>
          )}
        </div>
      )}
    </li>
  );
}

function StatusIcon({ status }: { status: GitBuildStatus }) {
  if (status === 'Succeeded') return <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-green-600" />;
  if (status === 'Failed') return <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />;
  if (status === 'Building') return <Loader2 className="mt-0.5 h-4 w-4 shrink-0 animate-spin text-amber-500" />;
  return <CircleDot className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />;
}

function StatusLabel({ status }: { status: GitBuildStatus }) {
  const { t } = useTranslation();
  const map: Record<GitBuildStatus, string> = {
    Queued: t('sites.buildQueued'),
    Building: t('sites.buildBuilding'),
    Succeeded: t('sites.buildSucceeded'),
    Failed: t('sites.buildFailed'),
  };
  return <span className="font-medium">{map[status]}</span>;
}
