import { describe, expect, it } from 'vitest';

import { mergeSeries } from './chartTheme';

const point = (day: string, events: number, visitors = events) => ({ day, events, visitors });

describe('mergeSeries', () => {
  it('pairs by position, not by date — the two windows never share dates', () => {
    const now = [point('2026-09-06', 10), point('2026-09-07', 20)];
    const before = [point('2026-08-06', 5), point('2026-08-07', 8)];
    expect(mergeSeries(now, before, 'events')).toEqual([
      { day: '2026-09-06', value: 10, comparison: 5 },
      { day: '2026-09-07', value: 20, comparison: 8 },
    ]);
  });

  it('aligns to the END, so the most recent day of each window lines up', () => {
    // Aligning from the start puts the windows out of step whenever one has a day fewer,
    // which happens at every month boundary.
    const now = [point('2026-09-06', 10), point('2026-09-07', 20)];
    const before = [point('2026-08-05', 1), point('2026-08-06', 5), point('2026-08-07', 8)];
    expect(mergeSeries(now, before, 'events').map((r) => r.comparison)).toEqual([5, 8]);
  });

  it('leaves a gap where the earlier window had no such day', () => {
    // A gap must stay null: the chart is told not to connect through it, because drawing a
    // line across would invent data.
    const now = [point('2026-09-05', 3), point('2026-09-06', 10)];
    const before = [point('2026-08-06', 5)];
    expect(mergeSeries(now, before, 'events').map((r) => r.comparison)).toEqual([null, 5]);
  });

  it('reports no comparison at all when the previous period has not loaded', () => {
    const now = [point('2026-09-06', 10)];
    expect(mergeSeries(now, undefined, 'events')).toEqual([
      { day: '2026-09-06', value: 10, comparison: null },
    ]);
  });

  it('reads the measure it was asked for', () => {
    const now = [{ day: '2026-09-06', events: 100, visitors: 7 }];
    expect(mergeSeries(now, undefined, 'visitors')[0].value).toBe(7);
  });
});
