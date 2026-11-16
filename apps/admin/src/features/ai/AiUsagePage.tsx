import { useQuery } from '@tanstack/react-query';
import { BarChart3 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Legend,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import {
  Badge,
  Callout,
  Card,
  CardContent,
  CenteredSpinner,
  EmptyState,
  LoadFailed,
  Page,
  PageHeader,
  SectionHeader,
  useTheme,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { useProviderChoices } from '../agent/ProviderPicker';
import {
  amount,
  byDay,
  daysBetween,
  groupSeries,
  type Metric,
  PARTS,
  partsByDay,
  type Series,
  totals,
  type UsageResponse,
} from './usage';

/*
 * The categorical palette, in fixed order, validated with the dataviz validator against the
 * surfaces it sits on (white card; #0f1521 dark card): lightness band, chroma, adjacent CVD and
 * normal-vision separation all pass in both modes. Three light steps sit under 3:1 contrast,
 * which is why every chart has a legend and the comparison has its table.
 */
const LIGHT = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#6250d6', '#e34948'];
const DARK = ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#008300', '#9085e9', '#e66767'];

const RANGES = [7, 30, 90] as const;

function useChart() {
  const { resolved } = useTheme();
  return {
    colors: resolved === 'dark' ? DARK : LIGHT,
    grid: 'hsl(var(--border))',
    axis: 'hsl(var(--muted-foreground))',
    // The 2px gap between stacked segments: a stroke in the card's own colour.
    gap: 'hsl(var(--card))',
    tooltip: {
      background: 'hsl(var(--popover))',
      border: '1px solid hsl(var(--border))',
      borderRadius: 8,
      fontSize: 12,
      color: 'hsl(var(--popover-foreground))',
    },
  };
}

/**
 * Settings → AI usage: what the workspace's AI providers were used for, day by day.
 *
 * <p>One chart of every provider together, a comparison of their totals (with the same numbers as
 * a table), and one small chart per provider split into the token kinds it bills. A member who
 * uses their own key sees it as a provider of its own; the workspace's spend needs ai:settings.</p>
 */
export function AiUsagePage() {
  const { t, i18n } = useTranslation();
  const [days, setDays] = useState<number>(30);
  const [metric, setMetric] = useState<Metric>('tokens');
  const usage = useQuery({
    queryKey: ['ai-usage', days],
    queryFn: () => api.get<UsageResponse>(`/admin/ai/usage?days=${days}`),
  });
  const choices = useProviderChoices();
  const chart = useChart();

  const compact = new Intl.NumberFormat(i18n.language, { notation: 'compact', maximumFractionDigits: 1 });
  const full = new Intl.NumberFormat(i18n.language);
  const dayLabel = (day: string) =>
    new Date(`${day}T00:00:00Z`).toLocaleDateString(i18n.language, { month: 'short', day: 'numeric', timeZone: 'UTC' });

  const filters = (
    <div className="flex flex-wrap items-center gap-2">
      <select
        value={days}
        onChange={(e) => setDays(Number(e.target.value))}
        aria-label={t('ai.usage.range')}
        className="h-9 rounded-md border bg-background px-2 text-sm"
      >
        {RANGES.map((n) => (
          <option key={n} value={n}>
            {t('ai.usage.lastDays', { count: n })}
          </option>
        ))}
      </select>
      <select
        value={metric}
        onChange={(e) => setMetric(e.target.value as Metric)}
        aria-label={t('ai.usage.metric')}
        className="h-9 rounded-md border bg-background px-2 text-sm"
      >
        <option value="tokens">{t('ai.usage.metricTokens')}</option>
        <option value="calls">{t('ai.usage.metricCalls')}</option>
      </select>
    </div>
  );

  if (usage.isLoading || usage.isError || !usage.data) {
    return (
      <Page>
        <PageHeader title={t('ai.usage.title')} description={t('ai.usage.description')} />
        {usage.isError ? <LoadFailed onRetry={() => void usage.refetch()} /> : <CenteredSpinner />}
      </Page>
    );
  }

  const data = usage.data;
  const range = daysBetween(data.from, data.to);
  const order = (choices.data?.providers ?? []).map((p) => p.id);
  const series = groupSeries([...(data.workspace ?? []), ...data.mine], order, {
    own: t('ai.usage.ownKey'),
    platform: t('ai.usage.platform'),
  });
  // A provider keeps its colour by its place in the whole list, never by what the range shows.
  const colorOf = (key: string) => chart.colors[Math.max(0, series.findIndex((s) => s.key === key)) % chart.colors.length];
  const stacked = byDay(series, range, metric, t('ai.usage.other'));
  const stackedColor = (key: string, i: number) => (key === 'other' ? 'hsl(var(--muted-foreground))' : colorOf(key) ?? chart.colors[i]);
  const sums = totals(series);
  const unit = metric === 'calls' ? t('ai.usage.calls') : t('ai.usage.tokens');

  const axisProps = {
    stroke: chart.axis,
    fontSize: 11,
    tickLine: false,
    axisLine: false,
  } as const;

  return (
    <Page>
      <PageHeader title={t('ai.usage.title')} description={t('ai.usage.description')} actions={filters} />

      <div className="space-y-6">
        {data.own ? (
          <Callout tone="info" title={t('ai.usage.ownTitle')}>
            {t('ai.usage.ownBody', {
              provider: data.own.provider === 'Inherit' ? t('ai.usage.workspaceKind') : data.own.provider,
              model: data.own.model ?? t('ai.usage.defaultModel'),
            })}{' '}
            {data.own.hasApiKey ? t('ai.usage.ownBilled') : t('ai.usage.ownNoKey')}
          </Callout>
        ) : null}
        {data.workspace === null ? <Callout tone="info">{t('ai.usage.workspaceHidden')}</Callout> : null}

        {series.length === 0 ? (
          <EmptyState icon={BarChart3} title={t('ai.usage.none')} description={t('ai.usage.noneHint')} />
        ) : (
          <>
            <Card>
              <CardContent>
                <SectionHeader title={t('ai.usage.allTitle', { unit })} description={t('ai.usage.allHint')} />
                <figure aria-label={t('ai.usage.allTitle', { unit })} className="h-72">
                  <ResponsiveContainer width="100%" height="100%">
                    <BarChart data={stacked.data} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
                      <CartesianGrid vertical={false} stroke={chart.grid} />
                      <XAxis dataKey="day" tickFormatter={dayLabel} minTickGap={24} {...axisProps} />
                      <YAxis tickFormatter={(v: number) => compact.format(v)} width={48} {...axisProps} />
                      <Tooltip
                        contentStyle={chart.tooltip}
                        cursor={{ fill: 'hsl(var(--muted))', opacity: 0.5 }}
                        labelFormatter={(day) => dayLabel(String(day))}
                        formatter={(value) => full.format(Number(value))}
                      />
                      <Legend wrapperStyle={{ fontSize: 12 }} formatter={(name) => <span className="text-foreground">{name}</span>} />
                      {stacked.keys.map((k, i) => (
                        <Bar
                          key={k.key}
                          dataKey={k.key}
                          name={k.name}
                          stackId="all"
                          fill={stackedColor(k.key, i)}
                          stroke={chart.gap}
                          strokeWidth={1}
                          maxBarSize={28}
                        />
                      ))}
                    </BarChart>
                  </ResponsiveContainer>
                </figure>
              </CardContent>
            </Card>

            <Card>
              <CardContent className="space-y-4">
                <SectionHeader title={t('ai.usage.compareTitle', { unit })} description={t('ai.usage.compareHint')} />
                <figure aria-label={t('ai.usage.compareTitle', { unit })} style={{ height: Math.max(96, sums.length * 44) }}>
                  <ResponsiveContainer width="100%" height="100%">
                    <BarChart data={sums} layout="vertical" margin={{ top: 0, right: 48, bottom: 0, left: 0 }}>
                      <CartesianGrid horizontal={false} stroke={chart.grid} />
                      <XAxis type="number" tickFormatter={(v: number) => compact.format(v)} {...axisProps} />
                      <YAxis type="category" dataKey="name" width={140} {...axisProps} fontSize={12} />
                      <Tooltip
                        contentStyle={chart.tooltip}
                        cursor={{ fill: 'hsl(var(--muted))', opacity: 0.5 }}
                        formatter={(value) => [full.format(Number(value)), unit]}
                      />
                      <Bar
                        dataKey={metric === 'calls' ? 'calls' : 'tokens'}
                        maxBarSize={24}
                        radius={[0, 4, 4, 0]}
                        label={{ position: 'right', fontSize: 11, fill: chart.axis, formatter: (v: unknown) => compact.format(Number(v)) }}
                      >
                        {sums.map((s) => (
                          <Cell key={s.key} fill={colorOf(s.key)} />
                        ))}
                      </Bar>
                    </BarChart>
                  </ResponsiveContainer>
                </figure>

                {/* The same numbers, readable without colour. */}
                <div className="overflow-x-auto">
                  <table className="w-full min-w-[36rem] text-sm">
                    <thead className="text-left text-xs text-muted-foreground">
                      <tr>
                        <th className="py-1.5 pr-3 font-medium">{t('ai.usage.provider')}</th>
                        <th className="py-1.5 pr-3 text-right font-medium">{t('ai.usage.metricCalls')}</th>
                        <th className="py-1.5 pr-3 text-right font-medium">{t('ai.usage.part.inputTokens')}</th>
                        <th className="py-1.5 pr-3 text-right font-medium">{t('ai.usage.part.outputTokens')}</th>
                        <th className="py-1.5 pr-3 text-right font-medium">{t('ai.usage.part.cacheWriteTokens')}</th>
                        <th className="py-1.5 pr-3 text-right font-medium">{t('ai.usage.part.cacheReadTokens')}</th>
                        <th className="py-1.5 text-right font-medium">{t('ai.usage.totalTokens')}</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y">
                      {sums.map((s) => (
                        <tr key={s.key}>
                          <td className="py-1.5 pr-3">
                            <span className="inline-flex items-center gap-2">
                              <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-sm" style={{ background: colorOf(s.key) }} />
                              <span className="[overflow-wrap:anywhere]">{s.name}</span>
                            </span>
                          </td>
                          <td className="py-1.5 pr-3 text-right tabular-nums">{full.format(s.calls)}</td>
                          <td className="py-1.5 pr-3 text-right tabular-nums">{full.format(s.inputTokens)}</td>
                          <td className="py-1.5 pr-3 text-right tabular-nums">{full.format(s.outputTokens)}</td>
                          <td className="py-1.5 pr-3 text-right tabular-nums">{full.format(s.cacheWriteTokens)}</td>
                          <td className="py-1.5 pr-3 text-right tabular-nums">{full.format(s.cacheReadTokens)}</td>
                          <td className="py-1.5 text-right font-medium tabular-nums">{full.format(s.tokens)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </CardContent>
            </Card>

            <section>
              <SectionHeader title={t('ai.usage.perProvider')} description={t('ai.usage.perProviderHint')} />
              <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
                {series.map((s) => (
                  <ProviderChart
                    key={s.key}
                    series={s}
                    range={range}
                    metric={metric}
                    color={colorOf(s.key)}
                    total={s.rows.reduce((n, r) => n + amount(r, metric), 0)}
                    format={{ compact, full, dayLabel }}
                  />
                ))}
              </div>
            </section>
          </>
        )}
      </div>
    </Page>
  );
}

/** One provider's days: its token kinds stacked (or its calls), titled by the provider. */
function ProviderChart({
  series,
  range,
  metric,
  color,
  total,
  format,
}: {
  series: Series;
  range: string[];
  metric: Metric;
  color: string;
  total: number;
  format: { compact: Intl.NumberFormat; full: Intl.NumberFormat; dayLabel: (day: string) => string };
}) {
  const { t } = useTranslation();
  const chart = useChart();
  const data = partsByDay(series.rows, range);
  const models = [...new Set(series.rows.map((r) => r.model).filter(Boolean))];
  const axisProps = { stroke: chart.axis, fontSize: 11, tickLine: false, axisLine: false } as const;

  return (
    <Card>
      <CardContent className="space-y-3">
        <div className="flex flex-wrap items-baseline gap-2">
          <span aria-hidden className="h-2.5 w-2.5 shrink-0 self-center rounded-sm" style={{ background: color }} />
          <h3 className="font-medium [overflow-wrap:anywhere]">{series.name}</h3>
          <Badge tone="outline">{series.kind}</Badge>
          <span className="ml-auto text-sm tabular-nums text-muted-foreground">
            {format.full.format(total)} {metric === 'calls' ? t('ai.usage.calls') : t('ai.usage.tokens')}
          </span>
        </div>
        {models.length > 0 ? (
          <p className="text-xs text-muted-foreground [overflow-wrap:anywhere]">{models.join(', ')}</p>
        ) : null}
        <figure aria-label={series.name} className="h-48">
          <ResponsiveContainer width="100%" height="100%">
            <BarChart data={data} margin={{ top: 4, right: 4, bottom: 0, left: 0 }}>
              <CartesianGrid vertical={false} stroke={chart.grid} />
              <XAxis dataKey="day" tickFormatter={format.dayLabel} minTickGap={24} {...axisProps} />
              <YAxis tickFormatter={(v: number) => format.compact.format(v)} width={44} {...axisProps} />
              <Tooltip
                contentStyle={chart.tooltip}
                cursor={{ fill: 'hsl(var(--muted))', opacity: 0.5 }}
                labelFormatter={(day) => format.dayLabel(String(day))}
                formatter={(value) => format.full.format(Number(value))}
              />
              {metric === 'calls' ? (
                <Bar dataKey="calls" name={t('ai.usage.calls')} fill={color} maxBarSize={20} radius={[4, 4, 0, 0]} />
              ) : (
                PARTS.map((part, i) => (
                  <Bar
                    key={part}
                    dataKey={part}
                    name={t(`ai.usage.part.${part}`)}
                    stackId="parts"
                    fill={chart.colors[i]}
                    stroke={chart.gap}
                    strokeWidth={1}
                    maxBarSize={20}
                  />
                ))
              )}
              {metric === 'calls' ? null : (
                <Legend wrapperStyle={{ fontSize: 11 }} formatter={(name) => <span className="text-foreground">{name}</span>} />
              )}
            </BarChart>
          </ResponsiveContainer>
        </figure>
      </CardContent>
    </Card>
  );
}
