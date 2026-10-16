import type { ReactNode } from 'react';
import { dateTime } from '@dcms/core';
import type { JobDto, JobLink, JobLinkRel } from '../live/jobs';

export interface JobDetailsLabels {
  /** One per link rel: "Source", "Configuration", "Run details", "Result". */
  rel: Record<JobLinkRel, string>;
  /** The link text when the link names nothing in particular. */
  open: string;
  trigger: string;
  queued: string;
  started: string;
  finished: string;
  duration: string;
  attempts: string;
  /** e.g. "1 of 3". */
  attemptsValue: (attempt: number, max: number) => string;
  progress: string;
  error: string;
  outcome: string;
  id: string;
}

const REL_ORDER: JobLinkRel[] = ['source', 'config', 'run', 'result'];

/**
 * Everything a job can say about itself, below its row on the jobs pages of both consoles
 * (ADR 0024): where it comes from and who or what started it, the configuration that defines it
 * and its own run details (as links where the app can follow them), when it ran, how often it
 * was tried, and what it produced. What each app owns is the words and the links: the admin
 * console routes them, the platform console only names them, because they are paths in the
 * tenant's console.
 */
export function JobDetails({
  job,
  labels,
  locale,
  trigger,
  stage,
  error,
  renderLink,
}: {
  job: JobDto;
  labels: JobDetailsLabels;
  locale?: string;
  /** "Schedule", "Started by ana@example.com", "Automatic". */
  trigger: string;
  /** The current stage, already in words. */
  stage?: string;
  /** The failure, already in words. */
  error?: string;
  renderLink: (link: JobLink, text: string) => ReactNode;
}) {
  const links = [...job.links].sort((a, b) => REL_ORDER.indexOf(a.rel) - REL_ORDER.indexOf(b.rel));
  const outcome = summarize(job.result);
  const ended = job.finishedAt ?? (job.status === 'Running' ? new Date().toISOString() : null);
  const took = job.startedAt && ended ? duration(Date.parse(ended) - Date.parse(job.startedAt)) : null;

  return (
    <dl
      className="grid grid-cols-1 gap-x-4 gap-y-1 px-3 pb-3 text-xs sm:grid-cols-[9rem_1fr]"
      data-testid="job-details"
    >
      {links.map((link) => (
        <Fact key={`${link.rel}:${link.path}`} label={labels.rel[link.rel] ?? link.rel}>
          {isAdminPath(link.path) ? renderLink(link, link.name ?? labels.open) : (link.name ?? link.path)}
        </Fact>
      ))}
      <Fact label={labels.trigger}>{trigger}</Fact>
      <Fact label={labels.queued}>{dateTime(job.createdAt, locale)}</Fact>
      {job.startedAt ? <Fact label={labels.started}>{dateTime(job.startedAt, locale)}</Fact> : null}
      {job.finishedAt ? <Fact label={labels.finished}>{dateTime(job.finishedAt, locale)}</Fact> : null}
      {took ? <Fact label={labels.duration}>{took}</Fact> : null}
      {job.attempts > 1 || job.status === 'Failed' ? (
        <Fact label={labels.attempts}>{labels.attemptsValue(job.attempts, job.maxAttempts)}</Fact>
      ) : null}
      {job.total !== null && job.status !== 'Succeeded' ? (
        <Fact label={labels.progress}>
          {job.done.toLocaleString(locale)} / {job.total.toLocaleString(locale)}
          {stage ? ` · ${stage}` : ''}
        </Fact>
      ) : null}
      {error ? (
        <Fact label={labels.error}>
          <span className="text-destructive">{error}</span>
        </Fact>
      ) : null}
      {outcome.length > 0 ? (
        <Fact label={labels.outcome}>
          {outcome.map(([key, value]) => (
            <span key={key} className="mr-3 inline-block">
              <span className="text-muted-foreground">{key}</span> {value}
            </span>
          ))}
        </Fact>
      ) : null}
      <Fact label={labels.id}>
        <span className="select-all break-all font-mono text-muted-foreground">{job.id}</span>
      </Fact>
    </dl>
  );
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <dt className="pt-1 text-muted-foreground sm:pt-0">{label}</dt>
      <dd className="min-w-0 break-words">{children}</dd>
    </>
  );
}

/**
 * The server's rule for a job link (`JobLinkRels.IsAdminPath`), again where it becomes a link:
 * one leading slash, then only characters a URL carries as they are. A browser drops tabs and
 * line breaks from an href, so `/\t/evil.example` would open another site as `//evil.example`.
 */
export function isAdminPath(path: string): boolean {
  return path.length <= 512 && /^\/(?![/\\])[A-Za-z0-9\-._~!$&'()*+,;=:@%/?#]*$/.test(path);
}

/**
 * A handler's result as a line of facts: its top-level numbers, words and flags, and the length
 * of any list (an import's errors). Nested objects are left out; the result is an answer to
 * "what did it do", not a dump.
 */
export function summarize(result: unknown): [string, string][] {
  if (!result || typeof result !== 'object' || Array.isArray(result)) return [];
  const out: [string, string][] = [];
  for (const [key, value] of Object.entries(result as Record<string, unknown>)) {
    if (typeof value === 'number' || typeof value === 'boolean') out.push([key, String(value)]);
    else if (typeof value === 'string' && value.length <= 80) out.push([key, value]);
    else if (Array.isArray(value)) out.push([key, String(value.length)]);
    if (out.length === 8) break;
  }
  return out;
}

/** "850 ms", "42 s", "3 min 5 s", "2 h 4 min". */
export function duration(ms: number): string {
  if (ms < 1000) return `${Math.max(0, Math.round(ms))} ms`;
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} min ${s % 60} s`;
  return `${Math.floor(m / 60)} h ${m % 60} min`;
}
