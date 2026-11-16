/** One day of AI usage for one payer, provider and model, as `GET /admin/ai/usage` returns it. */
export interface UsageRow {
  day: string;
  source: 'workspace' | 'platform' | 'user';
  providerId: string | null;
  providerName: string;
  providerKind: string;
  model: string;
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
}

export interface UsageResponse {
  from: string;
  to: string;
  /** Null when the caller may not see the workspace's spend (no ai:settings). */
  workspace: UsageRow[] | null;
  /** What the caller's own key spent. */
  mine: UsageRow[];
  /** The caller's own AI settings, when they override the workspace's. */
  own: { provider: string; model: string | null; hasApiKey: boolean } | null;
}

export type Metric = 'tokens' | 'calls';

/** The token kinds a provider bills separately, in the order they stack. */
export const PARTS = ['inputTokens', 'outputTokens', 'cacheWriteTokens', 'cacheReadTokens'] as const;
export type Part = (typeof PARTS)[number];

/** One provider's line on the page: a workspace provider, the platform default, or the caller's own key. */
export interface Series {
  key: string;
  name: string;
  kind: string;
  rows: UsageRow[];
}

export const OWN = 'own';
export const PLATFORM = 'platform';
export const OTHER = 'other';

function keyOf(row: UsageRow): string {
  if (row.source === 'user') return OWN;
  return row.providerId ?? PLATFORM;
}

export function amount(row: Pick<UsageRow, 'calls' | Part>, metric: Metric): number {
  return metric === 'calls' ? row.calls : PARTS.reduce((sum, part) => sum + row[part], 0);
}

/**
 * Rows grouped into one series per provider, in a fixed order: the workspace's providers as it
 * lists them, then deleted ones by name, then the platform default, then the caller's own key.
 * Fixed so a provider keeps its colour whatever the range shows.
 */
export function groupSeries(
  rows: readonly UsageRow[],
  order: readonly string[],
  labels: { own: string; platform: string },
): Series[] {
  const groups = new Map<string, Series>();
  for (const row of rows) {
    const key = keyOf(row);
    let series = groups.get(key);
    if (!series) {
      series = { key, name: row.providerName, kind: row.providerKind, rows: [] };
      groups.set(key, series);
    }
    series.rows.push(row);
    // Rows come oldest first, so the last name seen is the provider's current one.
    series.name = row.providerName;
    series.kind = row.providerKind;
  }
  for (const series of groups.values()) {
    if (series.key === OWN) series.name = labels.own;
    else if (series.key === PLATFORM) series.name = labels.platform;
  }
  const rank = (s: Series) => {
    if (s.key === OWN) return order.length + 2;
    if (s.key === PLATFORM) return order.length + 1;
    const i = order.indexOf(s.key);
    return i < 0 ? order.length : i;
  };
  return [...groups.values()].sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name));
}

/** Every day from `from` to `to`, inclusive, as yyyy-mm-dd. */
export function daysBetween(from: string, to: string): string[] {
  const days: string[] = [];
  const end = Date.parse(`${to}T00:00:00Z`);
  for (let t = Date.parse(`${from}T00:00:00Z`); t <= end; t += 86_400_000) {
    days.push(new Date(t).toISOString().slice(0, 10));
  }
  return days;
}

/**
 * One point per day with a value per series, for the stacked chart of all providers. Past
 * `max` series the rest fold into "Other": a ninth colour would not be one anyone can tell apart.
 */
export function byDay(
  series: readonly Series[],
  days: readonly string[],
  metric: Metric,
  otherLabel: string,
  max = 8,
): { data: Record<string, string | number>[]; keys: { key: string; name: string }[] } {
  const kept = series.length > max ? series.slice(0, max - 1) : series;
  const folded = series.length > max ? series.slice(max - 1) : [];
  const keys = kept.map((s) => ({ key: s.key, name: s.name }));
  if (folded.length > 0) keys.push({ key: OTHER, name: otherLabel });

  const index = new Map(days.map((day, i) => [day, i]));
  const data: Record<string, string | number>[] = days.map((day) => {
    const point: Record<string, string | number> = { day };
    for (const k of keys) point[k.key] = 0;
    return point;
  });
  const add = (key: string, rows: readonly UsageRow[]) => {
    for (const row of rows) {
      const i = index.get(row.day);
      if (i !== undefined) data[i][key] = (data[i][key] as number) + amount(row, metric);
    }
  };
  for (const s of kept) add(s.key, s.rows);
  for (const s of folded) add(OTHER, s.rows);
  return { data, keys };
}

/** One provider's days, split into what each kind of token added (or its calls). */
export function partsByDay(rows: readonly UsageRow[], days: readonly string[]): Record<string, string | number>[] {
  const index = new Map(days.map((day, i) => [day, i]));
  const data = days.map((day) => ({ day, calls: 0, inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0 }));
  for (const row of rows) {
    const i = index.get(row.day);
    if (i === undefined) continue;
    data[i].calls += row.calls;
    for (const part of PARTS) data[i][part] += row[part];
  }
  return data;
}

export interface Totals {
  key: string;
  name: string;
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheWriteTokens: number;
  cacheReadTokens: number;
  tokens: number;
  models: string[];
}

/** Each series summed over the range, for the comparison chart and its table. */
export function totals(series: readonly Series[]): Totals[] {
  return series.map((s) => {
    const sum: Totals = {
      key: s.key, name: s.name, calls: 0, inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0, tokens: 0, models: [],
    };
    const models = new Set<string>();
    for (const row of s.rows) {
      sum.calls += row.calls;
      for (const part of PARTS) sum[part] += row[part];
      if (row.model) models.add(row.model);
    }
    sum.tokens = PARTS.reduce((n, part) => n + sum[part], 0);
    sum.models = [...models].sort();
    return sum;
  });
}
