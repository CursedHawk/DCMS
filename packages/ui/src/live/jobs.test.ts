import { describe, expect, it } from 'vitest';
import { QueryClient } from '@tanstack/react-query';

import { patchJobCaches, type JobChangedPush, type JobDto, type JobPage } from './jobs';

type Job = JobDto;
type JobChanged = JobChangedPush;
const ROOT = ['jobs', 'acme'] as const;
const LIST = [...ROOT, 'list', 'active', 100];
const applyJobChanged = (qc: QueryClient, c: JobChanged) => patchJobCaches(qc, ROOT, c);

const job = (over: Partial<Job> = {}): Job => ({
  id: 'j1', tenantId: 't', tenantSlug: null, tenantName: null, parentId: null, kind: 'test', lane: 'tenant',
  source: 'admin-api', titleKey: 'k', params: {}, status: 'Running', done: 1, total: 10, stageKey: null,
  errorKey: null, errorDetail: null, linkPath: null, cancelable: true, cancelRequested: false, retryable: false,
  attempts: 1, maxAttempts: 3, actorUserId: null, createdAt: '2026-10-15T10:00:00Z', startedAt: null,
  finishedAt: null, updatedAt: '2026-10-15T10:00:00Z', seq: 5, result: null, ...over,
});

const change = (over: Partial<JobChanged> = {}): JobChanged => ({
  id: 'j1', tenantId: 't', parentId: null, status: 'Running', done: 4, total: 10, stageKey: 'writing', seq: 6, ...over,
});

function client(items: Job[]) {
  const qc = new QueryClient();
  qc.setQueryData<JobPage>(LIST, { items, nextCursor: null });
  return qc;
}

const listed = (qc: QueryClient) => qc.getQueryData<JobPage>(LIST)!.items[0];

describe('patchJobCaches', () => {
  it('patches a progress tick in place without asking for a refetch', () => {
    const qc = client([job()]);
    expect(applyJobChanged(qc, change())).toBe(false);
    expect(listed(qc)).toMatchObject({ done: 4, stageKey: 'writing', seq: 6 });
  });

  it('ignores a change older than what the cache holds', () => {
    const qc = client([job({ seq: 9, done: 7 })]);
    expect(applyJobChanged(qc, change({ seq: 8, done: 2 }))).toBe(false);
    expect(listed(qc).done).toBe(7);
  });

  it('asks for a refetch when a job finishes, because it changes lists', () => {
    const qc = client([job()]);
    expect(applyJobChanged(qc, change({ status: 'Succeeded', done: 10 }))).toBe(true);
    expect(listed(qc).cancelable).toBe(false);
  });

  it('asks for a refetch for a top-level job it has never seen, and not for a stranger child', () => {
    const qc = client([job()]);
    expect(applyJobChanged(qc, change({ id: 'new' }))).toBe(true);
    expect(applyJobChanged(qc, change({ id: 'child', parentId: 'p' }))).toBe(false);
  });
});
