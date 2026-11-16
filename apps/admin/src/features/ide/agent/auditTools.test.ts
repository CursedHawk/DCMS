import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AuditView } from '../../site-source/audits';

vi.mock('../../../lib/api', () => ({ api: { get: vi.fn(() => new Promise(() => {})), post: vi.fn() } }));
vi.mock('../../site-source/audits', async (actual) => ({
  ...(await actual<typeof import('../../site-source/audits')>()),
  auditsApi: { start: vi.fn(), get: vi.fn(), list: vi.fn() },
}));

const { AUDIT_TOOLS, formatAudit, formatIssue } = await import('./auditTools');
const { auditsApi } = await import('../../site-source/audits');
const { queryClient } = await import('../../../lib/queryClient');
const { jobKey } = await import('../../jobs/api');

const AUDIT: AuditView = {
  id: 'a1',
  target: 'draft',
  branch: 'main',
  url: null,
  status: 'Succeeded',
  error: null,
  jobId: 'j1',
  createdAt: '2026-11-16T20:00:00Z',
  completedAt: '2026-11-16T20:01:00Z',
  scores: { mobile: { performance: 42, seo: 90 }, desktop: { performance: 91, seo: 90 } },
  mobile: {
    strategy: 'mobile',
    finalUrl: null,
    scores: { performance: 42, seo: 90 },
    metrics: [],
    fieldCategory: null,
    field: [],
    warnings: [],
    issues: [
      {
        id: 'image-alt', category: 'accessibility', title: 'Image elements do not have [alt] attributes',
        description: 'Informative elements need alternate text.', score: 0, displayValue: null,
        savingsMs: null, savingsBytes: null, items: [{ selector: 'main > img', snippet: '<img src="hero.jpg">' }], itemCount: 3,
      },
    ],
  },
  desktop: null,
};

const context = () => ({ siteId: 's1', branch: 'main', flushDraft: vi.fn(async () => {}) }) as never;
const runTool = AUDIT_TOOLS.find((t) => t.name === 'run_site_audit')!;

afterEach(() => {
  vi.clearAllMocks();
  queryClient.clear();
});

describe('the site agent’s audit tools', () => {
  it('tell the model the scores and what failed, and one failure in full', () => {
    const text = formatAudit(AUDIT);
    expect(text).toContain('the unpublished changes (branch main)');
    expect(text).toContain('Mobile: performance 42, seo 90');
    expect(text).toContain('- [image-alt] (accessibility) Image elements do not have [alt] attributes');

    const issue = formatIssue(AUDIT, 'image-alt')!;
    expect(issue).toContain('Found (3 in all):');
    expect(issue).toContain('main > img · <img src="hero.jpg">');
    expect(formatIssue(AUDIT, 'nope')).toBeNull();
  });

  it('saves the draft, audits it and answers once its job is done', async () => {
    vi.mocked(auditsApi.start).mockResolvedValue({ auditId: 'a1', jobId: 'j1' });
    vi.mocked(auditsApi.get).mockResolvedValue(AUDIT);
    const ctx = context();

    const outcome = runTool.run({}, ctx);
    await vi.waitFor(() => expect(auditsApi.start).toHaveBeenCalledWith('s1', 'draft', 'main'));
    // The hub's patch of the job, as the notification hub applies it.
    queryClient.setQueryData(jobKey('j1'), { job: { status: 'Succeeded' }, children: [] });

    await expect(outcome).resolves.toMatchObject({ content: expect.stringContaining('audit a1') });
    expect((ctx as unknown as { flushDraft: ReturnType<typeof vi.fn> }).flushDraft).toHaveBeenCalled();
  });

  it('stops waiting when the run is stopped', async () => {
    vi.mocked(auditsApi.start).mockResolvedValue({ auditId: 'a1', jobId: 'j2' });
    const stop = new AbortController();

    const outcome = runTool.run({ target: 'live' }, context(), stop.signal);
    await vi.waitFor(() => expect(auditsApi.start).toHaveBeenCalledWith('s1', 'live', 'main'));
    stop.abort();

    await expect(outcome).rejects.toMatchObject({ name: 'AbortError' });
  });
});
