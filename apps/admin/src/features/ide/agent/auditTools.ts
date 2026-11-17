import { QueryObserver } from '@tanstack/react-query';
import type { JobDetail } from '@dcms/ui';
import type { ToolSpec } from '../../agent/contracts';
import { api } from '../../../lib/api';
import { Perm } from '../../../lib/permissions';
import { queryClient } from '../../../lib/queryClient';
import { jobKey } from '../../jobs/api';
import { auditsApi, type AuditReport, type AuditView } from '../../site-source/audits';
import type { CheckToolContext } from './checkTools';

/**
 * PageSpeed Insights for the site agent (#649), in every site kind: run an audit of the
 * unpublished changes (or the live site), read what failed, fix it, and run again to check.
 */

type AuditTool = ToolSpec<CheckToolContext>;

/** The audit's job ends on the server; this waits for that, as the jobs ring does. */
const WAIT_LIMIT_MS = 10 * 60_000;
const TERMINAL = new Set(['Succeeded', 'Failed', 'Canceled']);

function waitForJob(jobId: string, signal?: AbortSignal): Promise<void> {
  const observer = new QueryObserver(queryClient, {
    queryKey: jobKey(jobId),
    queryFn: () => api.get<JobDetail>(`/admin/jobs/${jobId}`),
  });
  return new Promise((resolve, reject) => {
    const finish = (error?: unknown) => {
      unsubscribe();
      clearInterval(recheck);
      clearTimeout(limit);
      signal?.removeEventListener('abort', aborted);
      if (error) reject(error);
      else resolve();
    };
    // The notification hub patches this query as the job moves (ADR 0024).
    const unsubscribe = observer.subscribe((result) => {
      if (result.data && TERMINAL.has(result.data.job.status)) finish();
    });
    // A push lost while the socket was down is never replayed; one slow re-check covers it
    // without asking every few seconds.
    const recheck = setInterval(() => void observer.refetch(), 30_000);
    const limit = setTimeout(() => finish(new Error('The audit did not finish within 10 minutes.')), WAIT_LIMIT_MS);
    const aborted = () => finish(new DOMException('The run was stopped.', 'AbortError'));
    if (signal?.aborted) return aborted();
    signal?.addEventListener('abort', aborted);
    void observer.refetch();
  });
}

const scoreLine = (scores: Record<string, number | null> | null | undefined) =>
  scores ? Object.entries(scores).map(([k, v]) => `${k} ${v ?? '–'}`).join(', ') : 'none';

function issueLines(report: AuditReport | null | undefined): string[] {
  if (!report || report.issues.length === 0) return ['  (every audit passed)'];
  return report.issues.map(
    (i) => `  - [${i.id}] (${i.category}) ${i.title}${i.displayValue ? ` — ${i.displayValue}` : ''}`,
  );
}

/** The whole of one audit, for the model: scores first, then what failed on each strategy. */
export function formatAudit(audit: AuditView): string {
  const what = audit.target === 'live' ? 'the live site' : `the unpublished changes${audit.branch ? ` (branch ${audit.branch})` : ''}`;
  if (audit.status === 'Failed') return `The audit of ${what} failed: ${audit.error ?? 'no reason given'}.`;
  return [
    `PageSpeed Insights audit ${audit.id} of ${what} (scores 0–100; 90+ is good):`,
    `Mobile: ${scoreLine(audit.scores.mobile)}`,
    `Desktop: ${scoreLine(audit.scores.desktop)}`,
    'Failing audits on mobile:',
    ...issueLines(audit.mobile),
    'Failing audits on desktop:',
    ...issueLines(audit.desktop),
    'Read one in full with get_site_audit { auditRef } before fixing it; run run_site_audit again to check the fix.',
  ].join('\n');
}

/** One failing audit in full: what it means and where on the page it found the problem. */
export function formatIssue(audit: AuditView, ref: string, strategy?: string): string | null {
  const reports = (strategy === 'desktop' ? [audit.desktop, audit.mobile] : [audit.mobile, audit.desktop]).filter(Boolean);
  for (const report of reports) {
    const issue = report!.issues.find((i) => i.id === ref);
    if (!issue) continue;
    const items = issue.items.map((item) =>
      `  - ${[item.url, item.selector, item.label, item.snippet, item.wastedBytes ? `${Math.round(item.wastedBytes / 1024)} KiB to save` : null, item.wastedMs ? `${Math.round(item.wastedMs)} ms to save` : null]
        .filter(Boolean)
        .join(' · ')}`,
    );
    return [
      `[${issue.id}] ${issue.title} (${issue.category}, ${report!.strategy})`,
      issue.displayValue ?? '',
      issue.description,
      items.length ? `Found (${issue.itemCount} in all):` : 'No specific elements listed.',
      ...items,
    ]
      .filter((line) => line !== '')
      .join('\n');
  }
  return null;
}

export const AUDIT_TOOLS: AuditTool[] = [
  {
    name: 'run_site_audit',
    description:
      'Test the site with Google PageSpeed Insights — performance, accessibility, best practices and SEO, on mobile and desktop — and get its scores and the audits that failed. target "draft" (the default) builds and tests your current changes, not yet published, at a temporary address; "live" tests the published site. Takes one to three minutes. Then read an issue in full with get_site_audit, fix it, and run again to check.',
    permission: Perm.SiteEdit,
    // It builds the changes and serves them for an hour at an unguessable address: no publish.
    risk: 'safe',
    invalidates: ['site-audits'],
    // Scores and Google's audit titles only; the page's own URLs and markup come with get_site_audit.
    maxResultChars: 8000,
    input_schema: {
      type: 'object',
      properties: { target: { type: 'string', enum: ['draft', 'live'], description: 'draft (default) or live' } },
      additionalProperties: false,
    },
    summarize: (input) => `Audit ${input.target === 'live' ? 'the live site' : 'the unpublished changes'} with PageSpeed Insights`,
    describe: (input) => `Audited ${input.target === 'live' ? 'the live site' : 'the unpublished changes'}`,
    run: async (input, ctx, signal) => {
      const target = input.target === 'live' ? 'live' : 'draft';
      // What is built is the saved draft: the run's own edits go in first.
      if (target === 'draft') await ctx.flushDraft();
      try {
        const { auditId, jobId } = await auditsApi.start(ctx.siteId, target, ctx.branch);
        await waitForJob(jobId, signal);
        return { content: formatAudit(await auditsApi.get(ctx.siteId, auditId)) };
      } catch (error) {
        if ((error as Error)?.name === 'AbortError') throw error;
        return { content: `The audit could not run: ${(error as Error)?.message ?? error}`, isError: true };
      }
    },
  },
  {
    name: 'get_site_audit',
    description:
      "A PageSpeed Insights audit of this site: the latest one, or auditId's. With auditRef (an id from its failing audits, e.g. \"image-alt\"), that one audit in full — what it means and the elements, files and savings it found — to act on.",
    permission: Perm.SiteEdit,
    maxResultChars: 8000,
    untrustedSource: 'a PageSpeed Insights audit of the site — its own URLs and markup',
    input_schema: {
      type: 'object',
      properties: {
        auditId: { type: 'string', description: 'An audit id; the latest finished one when omitted.' },
        auditRef: { type: 'string', description: 'One failing audit, e.g. "render-blocking-resources".' },
        strategy: { type: 'string', enum: ['mobile', 'desktop'] },
      },
      additionalProperties: false,
    },
    run: async (input, ctx) => {
      let id = typeof input.auditId === 'string' ? input.auditId : undefined;
      if (!id) {
        const { audits } = await auditsApi.list(ctx.siteId);
        id = audits.find((a) => a.status === 'Succeeded')?.id;
        if (!id) return { content: 'This site has no finished audit yet. Run one with run_site_audit.' };
      }
      const audit = await auditsApi.get(ctx.siteId, id);
      if (typeof input.auditRef === 'string') {
        const detail = formatIssue(audit, input.auditRef, typeof input.strategy === 'string' ? input.strategy : undefined);
        return detail
          ? { content: detail }
          : { content: `No failing audit "${input.auditRef}" in audit ${audit.id}.`, isError: true };
      }
      return { content: formatAudit(audit) };
    },
  },
];
