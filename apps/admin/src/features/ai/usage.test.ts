import { describe, expect, it } from 'vitest';
import { byDay, daysBetween, groupSeries, partsByDay, totals, type UsageRow } from './usage';

const row = (over: Partial<UsageRow>): UsageRow => ({
  day: '2026-11-15',
  source: 'workspace',
  providerId: 'a',
  providerName: 'A',
  providerKind: 'anthropic',
  model: 'm',
  calls: 1,
  inputTokens: 10,
  outputTokens: 2,
  cacheReadTokens: 100,
  cacheWriteTokens: 5,
  ...over,
});
const labels = { own: 'Your own key', platform: 'Platform default' };

describe('AI usage', () => {
  it('orders series as the workspace lists its providers, then deleted ones, the platform and your own key', () => {
    const series = groupSeries(
      [
        row({ source: 'user', providerId: null }),
        row({ source: 'platform', providerId: null, providerName: 'Platform default' }),
        row({ providerId: 'gone', providerName: 'Old' }),
        row({ providerId: 'b', providerName: 'B' }),
        row({ providerId: 'a', providerName: 'A (renamed)', day: '2026-11-16' }),
      ],
      ['b', 'a'],
      labels,
    );
    expect(series.map((s) => s.name)).toEqual(['B', 'A (renamed)', 'Old', 'Platform default', 'Your own key']);
  });

  it('fills every day of the range and folds past eight providers into Other', () => {
    const days = daysBetween('2026-11-14', '2026-11-16');
    expect(days).toEqual(['2026-11-14', '2026-11-15', '2026-11-16']);

    const rows = Array.from({ length: 9 }, (_, i) => row({ providerId: `p${i}`, providerName: `P${i}` }));
    const { data, keys } = byDay(groupSeries(rows, rows.map((r) => r.providerId!), labels), days, 'calls', 'Other');
    expect(keys.map((k) => k.name)).toEqual(['P0', 'P1', 'P2', 'P3', 'P4', 'P5', 'P6', 'Other']);
    expect(data[1].other).toBe(2);
    expect(data[0].p0).toBe(0);
  });

  it('adds token kinds per day and totals per provider', () => {
    const rows = [row({}), row({ model: 'n', calls: 3 })];
    expect(partsByDay(rows, ['2026-11-15'])[0]).toMatchObject({ calls: 4, inputTokens: 20, cacheReadTokens: 200 });
    expect(totals(groupSeries(rows, ['a'], labels))[0]).toMatchObject({ calls: 4, tokens: 234, models: ['m', 'n'] });
  });
});
