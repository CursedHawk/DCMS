import { describe, expect, it } from 'vitest';
import { jobFraction, overallProgress, type IndicatorJob } from './JobsIndicator';

const job = (over: Partial<IndicatorJob>): IndicatorJob => ({
  id: 'x', status: 'Running', done: 0, total: null, cancelable: true, cancelRequested: false,
  retryable: false, createdAt: '2026-10-15T10:00:00Z', finishedAt: null, ...over,
});

describe('the jobs ring', () => {
  it('is empty when nothing is active', () => {
    expect(overallProgress([])).toBe(0);
  });

  it('averages the running jobs that know their size', () => {
    expect(overallProgress([
      job({ done: 1, total: 4 }),
      job({ done: 3, total: 4 }),
      job({ total: null }),
      job({ status: 'Queued', done: 0, total: 10 }),
    ])).toBe(0.5);
  });

  it('turns (null) when work is active but none of it can say how far along it is', () => {
    expect(overallProgress([job({ total: null }), job({ status: 'Queued' })])).toBeNull();
  });

  it('reads a finished job as whole and clamps an over-reported one', () => {
    expect(jobFraction(job({ status: 'Succeeded', done: 0, total: null }))).toBe(1);
    expect(jobFraction(job({ done: 12, total: 10 }))).toBe(1);
  });
});
