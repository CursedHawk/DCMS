import { api } from '../../lib/api';

/**
 * PageSpeed Insights audits of a site (#649), as the console and the site agent read them. The
 * server keeps a summary per strategy: scores, lab metrics, field data, and the audits that failed.
 */

export type AuditStrategy = 'mobile' | 'desktop';
export type AuditTarget = 'live' | 'draft';
export type AuditStatus = 'Queued' | 'Building' | 'Testing' | 'Succeeded' | 'Failed';

export interface AuditItem {
  url?: string | null;
  selector?: string | null;
  snippet?: string | null;
  label?: string | null;
  wastedBytes?: number | null;
  wastedMs?: number | null;
  totalBytes?: number | null;
}

export interface AuditIssue {
  id: string;
  category: string;
  title: string;
  description: string;
  score: number | null;
  displayValue: string | null;
  savingsMs: number | null;
  savingsBytes: number | null;
  items: AuditItem[];
  itemCount: number;
}

export interface AuditReport {
  strategy: AuditStrategy;
  finalUrl: string | null;
  scores: Record<string, number | null>;
  metrics: { id: string; title: string; displayValue: string | null; score: number | null }[];
  fieldCategory: string | null;
  field: { id: string; percentile: number | null; category: string | null }[];
  issues: AuditIssue[];
  warnings: string[];
}

export interface AuditView {
  id: string;
  target: AuditTarget;
  branch: string | null;
  /** The live address; never an unpublished draft's. */
  url: string | null;
  status: AuditStatus;
  error: string | null;
  jobId: string | null;
  createdAt: string;
  completedAt: string | null;
  scores: { mobile: Record<string, number | null> | null; desktop: Record<string, number | null> | null };
  mobile?: AuditReport | null;
  desktop?: AuditReport | null;
}

export const CATEGORIES = ['performance', 'accessibility', 'best-practices', 'seo'] as const;

export const auditsKey = (siteId: string) => ['site-audits', siteId] as const;

export const auditsApi = {
  list: (siteId: string) => api.get<{ configured: boolean; audits: AuditView[] }>(`/admin/sites/${siteId}/audits`),
  get: (siteId: string, auditId: string) => api.get<AuditView>(`/admin/sites/${siteId}/audits/${auditId}`),
  start: (siteId: string, target: AuditTarget, branch?: string) =>
    api.post<{ auditId: string; jobId: string }>(`/admin/sites/${siteId}/audits`, { target, branch }),
};

export const isRunning = (status: AuditStatus) => status === 'Queued' || status === 'Building' || status === 'Testing';

/** Lighthouse's bands: 0–49 poor, 50–89 needs improvement, 90–100 good. */
export function band(score: number | null | undefined): 'poor' | 'average' | 'good' | 'none' {
  if (score == null) return 'none';
  return score >= 90 ? 'good' : score >= 50 ? 'average' : 'poor';
}

/** A Lighthouse description's text, its markdown links split out (`[Learn more](https://…)`). */
export function linkParts(markdown: string): ({ text: string } | { text: string; href: string })[] {
  const parts: ({ text: string } | { text: string; href: string })[] = [];
  const pattern = /\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)/g;
  let last = 0;
  for (const match of markdown.matchAll(pattern)) {
    if (match.index > last) parts.push({ text: markdown.slice(last, match.index) });
    parts.push({ text: match[1], href: match[2] });
    last = match.index + match[0].length;
  }
  if (last < markdown.length) parts.push({ text: markdown.slice(last) });
  return parts;
}

/** Google's own report of a public address, for the whole Lighthouse detail. */
export const pageSpeedUrl = (url: string) => `https://pagespeed.web.dev/analysis?url=${encodeURIComponent(url)}`;
