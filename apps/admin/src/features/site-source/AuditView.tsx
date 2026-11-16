import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ExternalLink, Gauge, Loader2 } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Callout, ProgressRing, cn, toastApiError, useHubRevalidation } from '@dcms/ui';
import { useJob } from '../jobs/api';
import { NOTIFICATION_HUB } from '../notifications/useNotificationHub';
import {
  CATEGORIES,
  auditsApi,
  auditsKey,
  band,
  isRunning,
  linkParts,
  pageSpeedUrl,
  type AuditIssue,
  type AuditReport,
  type AuditStrategy,
  type AuditTarget,
} from './audits';

const BAND_ARC = { good: 'stroke-success', average: 'stroke-warning', poor: 'stroke-destructive', none: 'stroke-muted' } as const;
const BAND_TEXT = { good: 'text-success', average: 'text-warning', poor: 'text-destructive', none: 'text-muted-foreground' } as const;

/**
 * PageSpeed Insights in every site editor's rail (#649): audit the live site or what is not
 * published yet (built for the purpose and served at a temporary address), then read the scores
 * and what failed. The site agent has the same audits as tools.
 */
export function AuditView({ siteId, branch }: { siteId: string; branch?: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [target, setTarget] = useState<AuditTarget>('draft');
  const [selected, setSelected] = useState<string | null>(null);
  const [strategy, setStrategy] = useState<AuditStrategy>('mobile');

  // A running audit's job arrives over the notification hub (its stage, then its end, which
  // refetches this); a push missed while the socket was down is caught on reconnect and focus.
  const list = useQuery({ queryKey: auditsKey(siteId), queryFn: () => auditsApi.list(siteId) });
  useHubRevalidation({ hub: NOTIFICATION_HUB, keys: [auditsKey(siteId)] });
  const audits = list.data?.audits ?? [];
  const running = audits.find((a) => isRunning(a.status));
  const job = useJob(running?.jobId ?? undefined);
  const jobDone = job.data && ['Succeeded', 'Failed', 'Canceled'].includes(job.data.job.status);
  useEffect(() => {
    if (jobDone) void queryClient.invalidateQueries({ queryKey: auditsKey(siteId) });
  }, [jobDone, queryClient, siteId]);

  const shownId = selected ?? audits.find((a) => a.status === 'Succeeded')?.id;
  const detail = useQuery({
    queryKey: [...auditsKey(siteId), 'detail', shownId],
    queryFn: () => auditsApi.get(siteId, shownId!),
    enabled: !!shownId,
  });
  const start = useMutation({
    mutationFn: () => auditsApi.start(siteId, target, branch),
    onSuccess: () => {
      setSelected(null);
      void queryClient.invalidateQueries({ queryKey: auditsKey(siteId) });
    },
    onError: (e) => toastApiError(e, t),
  });

  const latest = audits[0];
  const report = detail.data?.[strategy] ?? null;

  return (
    <div className="flex h-full flex-col overflow-auto">
      <div className="flex shrink-0 items-center gap-2 border-b p-2">
        <span className="flex-1 text-2xs font-semibold uppercase tracking-wide text-muted-foreground">{t('siteAudit.title')}</span>
        <Button
          size="sm"
          onClick={() => start.mutate()}
          disabled={!list.data?.configured || !!running || start.isPending}
        >
          <Gauge className="h-3.5 w-3.5" />
          {t('siteAudit.run')}
        </Button>
      </div>

      <div className="space-y-3 p-2 text-xs">
        {list.data && !list.data.configured && <Callout tone="warning">{t('siteAudit.notConfigured')}</Callout>}

        <div role="group" aria-label={t('siteAudit.target')} className="flex gap-1">
          {(['draft', 'live'] as const).map((value) => (
            <Button
              key={value}
              size="sm"
              variant={target === value ? 'default' : 'outline'}
              aria-pressed={target === value}
              className="h-7 flex-1 px-2 text-xs"
              onClick={() => setTarget(value)}
            >
              {t(`siteAudit.targets.${value}`)}
            </Button>
          ))}
        </div>
        <p className="text-2xs text-muted-foreground">{t(`siteAudit.targetHint.${target}`)}</p>

        {running && (
          <p className="flex items-center gap-1.5 text-muted-foreground">
            <Loader2 className="h-3.5 w-3.5 animate-spin" />
            {job.data?.job.stageKey ? t(job.data.job.stageKey) : t('siteAudit.queued')}
          </p>
        )}
        {!running && latest?.status === 'Failed' && (
          <Callout tone="destructive" title={t('siteAudit.failed')}>
            {latest.error}
          </Callout>
        )}

        {list.isLoading && <p className="text-muted-foreground">{t('common.loading')}</p>}
        {list.data && audits.length === 0 && !running && <p className="text-muted-foreground">{t('siteAudit.none')}</p>}

        {detail.data && (
          <section aria-label={t('siteAudit.result')} className="space-y-3">
            <div className="flex items-center gap-1">
              <span className="min-w-0 flex-1 truncate text-2xs text-muted-foreground">
                {t(`siteAudit.targets.${detail.data.target}`)} · {new Date(detail.data.createdAt).toLocaleString()}
              </span>
              {(['mobile', 'desktop'] as const).map((value) => (
                <Button
                  key={value}
                  size="sm"
                  variant={strategy === value ? 'secondary' : 'ghost'}
                  aria-pressed={strategy === value}
                  className="h-6 px-2 text-2xs"
                  onClick={() => setStrategy(value)}
                >
                  {t(`siteAudit.strategies.${value}`)}
                </Button>
              ))}
            </div>
            {report && <Report report={report} />}
            {detail.data.url && (
              <a className="inline-flex items-center gap-1 text-primary hover:underline" href={pageSpeedUrl(detail.data.url)} target="_blank" rel="noreferrer">
                <ExternalLink className="h-3 w-3" />
                {t('siteAudit.openInPsi')}
              </a>
            )}
          </section>
        )}

        {audits.length > 1 && (
          <section aria-label={t('siteAudit.history')}>
            <h3 className="mb-1 font-semibold">{t('siteAudit.history')}</h3>
            <ul className="space-y-0.5">
              {audits.map((a) => (
                <li key={a.id}>
                  <button
                    type="button"
                    disabled={a.status !== 'Succeeded'}
                    onClick={() => setSelected(a.id)}
                    className={cn(
                      'flex w-full items-center gap-2 rounded px-1 py-0.5 text-left hover:bg-accent disabled:opacity-60',
                      a.id === shownId && 'bg-accent',
                    )}
                  >
                    <span className="min-w-0 flex-1 truncate">
                      {new Date(a.createdAt).toLocaleString()} · {t(`siteAudit.targets.${a.target}`)}
                    </span>
                    {a.status === 'Succeeded' ? (
                      <span className={cn('font-semibold', BAND_TEXT[band(a.scores[strategy]?.performance)])}>
                        {a.scores[strategy]?.performance ?? '–'}
                      </span>
                    ) : (
                      <span className="text-muted-foreground">{t(`siteAudit.status.${a.status}`)}</span>
                    )}
                  </button>
                </li>
              ))}
            </ul>
          </section>
        )}
      </div>
    </div>
  );
}

function Report({ report }: { report: AuditReport }) {
  const { t } = useTranslation();
  return (
    <>
      <div className="grid grid-cols-4 gap-1">
        {CATEGORIES.map((category) => {
          const score = report.scores[category] ?? null;
          return (
            <div key={category} className="flex flex-col items-center gap-0.5 text-center">
              <ProgressRing
                value={score == null ? 0 : score / 100}
                size={40}
                stroke={3.5}
                arcClassName={BAND_ARC[band(score)]}
                label={`${t(`siteAudit.categories.${category}`)}: ${score ?? '–'}`}
              >
                <span className={cn('text-xs', BAND_TEXT[band(score)])}>{score ?? '–'}</span>
              </ProgressRing>
              <span className="text-2xs leading-tight text-muted-foreground">{t(`siteAudit.categories.${category}`)}</span>
            </div>
          );
        })}
      </div>

      <dl className="grid grid-cols-[1fr_auto] gap-x-2 gap-y-0.5">
        {report.metrics.map((m) => (
          <div key={m.id} className="contents">
            <dt className="truncate text-muted-foreground">{m.title}</dt>
            <dd className={cn('text-right font-medium', BAND_TEXT[band(m.score == null ? null : m.score * 100)])}>{m.displayValue ?? '–'}</dd>
          </div>
        ))}
      </dl>
      {report.fieldCategory && (
        <p className="text-2xs text-muted-foreground">{t('siteAudit.field', { category: report.fieldCategory.toLowerCase() })}</p>
      )}

      {report.issues.length === 0 ? (
        <p className="text-success">{t('siteAudit.allPassed')}</p>
      ) : (
        CATEGORIES.map((category) => {
          const issues = report.issues.filter((i) => i.category === category);
          if (issues.length === 0) return null;
          return (
            <section key={category} aria-label={t(`siteAudit.categories.${category}`)}>
              <h3 className="mb-1 font-semibold">
                {t(`siteAudit.categories.${category}`)} · {issues.length}
              </h3>
              <ul className="space-y-1">
                {issues.map((issue) => (
                  <Issue key={issue.id} issue={issue} />
                ))}
              </ul>
            </section>
          );
        })
      )}
    </>
  );
}

function Issue({ issue }: { issue: AuditIssue }) {
  const { t } = useTranslation();
  return (
    <li>
      <details className="rounded border px-1.5 py-1">
        <summary className="cursor-pointer">
          <span className={cn('font-medium', BAND_TEXT[band(issue.score == null ? null : issue.score * 100)])}>{issue.title}</span>
          {issue.displayValue && <span className="block text-2xs text-muted-foreground">{issue.displayValue}</span>}
        </summary>
        <p className="mt-1 text-2xs text-muted-foreground">
          {linkParts(issue.description).map((part, i) =>
            'href' in part ? (
              <a key={i} className="text-primary hover:underline" href={part.href} target="_blank" rel="noreferrer">
                {part.text}
              </a>
            ) : (
              <span key={i}>{part.text}</span>
            ),
          )}
        </p>
        {issue.items.length > 0 && (
          <ul className="mt-1 space-y-0.5 text-2xs">
            {issue.items.map((item, i) => (
              <li key={i} className="break-all font-mono">
                {item.url ?? item.selector ?? item.label}
                {item.snippet && <code className="block text-muted-foreground">{item.snippet}</code>}
              </li>
            ))}
            {issue.itemCount > issue.items.length && (
              <li className="text-muted-foreground">{t('siteAudit.moreItems', { count: issue.itemCount - issue.items.length })}</li>
            )}
          </ul>
        )}
      </details>
    </li>
  );
}
