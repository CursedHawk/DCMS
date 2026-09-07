import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Trash2, TrendingUp, X } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Area,
  AreaChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip as RTooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { toast } from 'sonner';
import {
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  CenteredSpinner,
  ConfirmDeleteDialog,
  EmptyState,
  Input,
  Page,
  PageHeader,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Table,
  TBody,
  TD,
  TH,
  THead,
  toastApiError,
  TR,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { type MyPermissions, Perm, can, useMyPermissions } from '../../lib/permissions';

interface Analytics {
  range: { from: string; to: string };
  summary: { events: number; pageviews: number; visitors: number; sessions: number };
  series: { day: string; events: number; visitors: number }[];
  topPaths: { path: string; count: number }[];
  byType: { type: string; count: number }[];
  topSources: { source: string; count: number }[];
  byCountry: { country: string; count: number }[];
  byDevice: { device: string; count: number }[];
  byBrowser: { browser: string; count: number }[];
  byCampaign: { source: string; medium: string | null; campaign: string | null; count: number }[];
}

interface Dimensions {
  types: string[];
  countries: string[];
  devices: string[];
}

/** Radix Select reserves the empty string for "nothing selected". */
const ANY = '__any';

/** ISO 3166-1 alpha-2 → the reader's own language, falling back to the raw code. */
const regionNames =
  typeof Intl !== 'undefined' && 'DisplayNames' in Intl
    ? new Intl.DisplayNames(undefined, { type: 'region' })
    : null;

function countryName(code: string): string {
  try {
    return regionNames?.of(code) ?? code;
  } catch {
    return code;
  }
}

export function AnalyticsPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const me = useMyPermissions(true);

  const [days, setDays] = useState('30');
  const [type, setType] = useState(ANY);
  const [country, setCountry] = useState(ANY);
  const [device, setDevice] = useState(ANY);
  const [path, setPath] = useState('');
  const [clearOpen, setClearOpen] = useState(false);

  // `path` is applied on submit rather than per keystroke: it is a prefix filter
  // over the raw event table, and refetching on every character would hammer it.
  const [appliedPath, setAppliedPath] = useState('');

  const query = new URLSearchParams({ days });
  if (type !== ANY) query.set('type', type);
  if (country !== ANY) query.set('country', country);
  if (device !== ANY) query.set('device', device);
  if (appliedPath.trim()) query.set('path', appliedPath.trim());

  const a = useQuery({
    queryKey: ['analytics', query.toString()],
    queryFn: () => api.get<Analytics>(`/admin/analytics?${query}`),
  });
  const dimensions = useQuery({
    queryKey: ['analytics-dimensions', days],
    queryFn: () => api.get<Dimensions>(`/admin/analytics/dimensions?days=${days}`),
  });

  const clear = useMutation({
    mutationFn: () => api.del<{ deletedEvents: number }>('/admin/analytics'),
    onSuccess: async (r) => {
      setClearOpen(false);
      toast.success(t('analytics.cleared', { count: r?.deletedEvents ?? 0 }));
      await qc.invalidateQueries({ queryKey: ['analytics'] });
      await qc.invalidateQueries({ queryKey: ['analytics-dimensions'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const hasFilters = type !== ANY || country !== ANY || device !== ANY || appliedPath.trim() !== '';
  const clearFilters = () => {
    setType(ANY);
    setCountry(ANY);
    setDevice(ANY);
    setPath('');
    setAppliedPath('');
  };

  return (
    <Page className="max-w-7xl">
      <PageHeader
        title={t('analytics.title')}
        actions={
          <div className="flex flex-wrap items-center gap-2">
            <Select value={days} onValueChange={setDays}>
              <SelectTrigger className="w-40">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {['7', '30', '90', '365'].map((d) => (
                  <SelectItem key={d} value={d}>
                    {t('analytics.days', { count: Number(d) })}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {/* Clearing history is a workspace-level act, not a reporting one, so it
                takes tenant:settings rather than analytics:read. */}
            {canClear(me.data) ? (
              <Button variant="outline" onClick={() => setClearOpen(true)}>
                <Trash2 className="h-4 w-4" /> {t('analytics.clear')}
              </Button>
            ) : null}
          </div>
        }
      />

      <div className="mb-5 flex flex-wrap items-center gap-2">
        <FilterSelect
          value={type}
          onChange={setType}
          placeholder={t('analytics.filterType')}
          options={dimensions.data?.types ?? []}
        />
        <FilterSelect
          value={device}
          onChange={setDevice}
          placeholder={t('analytics.filterDevice')}
          options={dimensions.data?.devices ?? []}
          label={(v) => t(`analytics.device.${v}`, v)}
        />
        <FilterSelect
          value={country}
          onChange={setCountry}
          placeholder={t('analytics.filterCountry')}
          options={dimensions.data?.countries ?? []}
          label={countryName}
        />
        <form
          className="min-w-0 flex-1"
          onSubmit={(e) => {
            e.preventDefault();
            setAppliedPath(path);
          }}
        >
          <Input
            value={path}
            onChange={(e) => setPath(e.target.value)}
            onBlur={() => setAppliedPath(path)}
            placeholder={t('analytics.filterPath')}
            className="h-9"
          />
        </form>
        {hasFilters ? (
          <Button size="sm" variant="ghost" onClick={clearFilters}>
            <X className="h-4 w-4" /> {t('analytics.clearFilters')}
          </Button>
        ) : null}
      </div>

      {a.isLoading ? (
        <CenteredSpinner />
      ) : !a.data || a.data.summary.events === 0 ? (
        <EmptyState
          icon={TrendingUp}
          title={t('analytics.title')}
          description={hasFilters ? t('analytics.noneForFilters') : t('analytics.noneYet')}
        />
      ) : (
        <div className="space-y-6">
          <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
            <StatCard label={t('analytics.visitors')} value={a.data.summary.visitors} />
            <StatCard label={t('analytics.sessions')} value={a.data.summary.sessions} />
            <StatCard label={t('analytics.pageviews')} value={a.data.summary.pageviews} />
            <StatCard label={t('analytics.events')} value={a.data.summary.events} />
          </div>

          <Card>
            <CardHeader>
              <CardTitle>{t('analytics.overTime')}</CardTitle>
            </CardHeader>
            <CardContent>
              <div className="h-72 w-full">
                <ResponsiveContainer width="100%" height="100%">
                  <AreaChart data={a.data.series} margin={{ left: -20, right: 8, top: 8 }}>
                    <defs>
                      <linearGradient id="ev" x1="0" y1="0" x2="0" y2="1">
                        <stop offset="0%" stopColor="#6366f1" stopOpacity={0.35} />
                        <stop offset="100%" stopColor="#6366f1" stopOpacity={0} />
                      </linearGradient>
                      <linearGradient id="vi" x1="0" y1="0" x2="0" y2="1">
                        <stop offset="0%" stopColor="#10b981" stopOpacity={0.3} />
                        <stop offset="100%" stopColor="#10b981" stopOpacity={0} />
                      </linearGradient>
                    </defs>
                    <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                    <XAxis dataKey="day" tick={{ fontSize: 11 }} stroke="hsl(var(--muted-foreground))" />
                    <YAxis tick={{ fontSize: 11 }} stroke="hsl(var(--muted-foreground))" allowDecimals={false} />
                    <RTooltip
                      contentStyle={{
                        background: 'hsl(var(--popover))',
                        border: '1px solid hsl(var(--border))',
                        borderRadius: 8,
                        fontSize: 12,
                      }}
                    />
                    <Area
                      type="monotone"
                      dataKey="events"
                      name={t('analytics.events')}
                      stroke="#6366f1"
                      strokeWidth={2}
                      fill="url(#ev)"
                    />
                    <Area
                      type="monotone"
                      dataKey="visitors"
                      name={t('analytics.visitors')}
                      stroke="#10b981"
                      strokeWidth={2}
                      fill="url(#vi)"
                    />
                  </AreaChart>
                </ResponsiveContainer>
              </div>
            </CardContent>
          </Card>

          <div className="grid gap-6 lg:grid-cols-2">
            <BreakdownCard
              title={t('analytics.topPaths')}
              rows={a.data.topPaths.map((r) => ({ key: r.path, label: r.path, count: r.count }))}
              onPick={(key) => {
                setPath(key);
                setAppliedPath(key);
              }}
            />
            <BreakdownCard
              title={t('analytics.topSources')}
              rows={a.data.topSources.map((r) => ({ key: r.source, label: r.source, count: r.count }))}
              empty={t('analytics.directOnly')}
            />
            <BreakdownCard
              title={t('analytics.byCountry')}
              rows={a.data.byCountry.map((r) => ({
                key: r.country,
                label: countryName(r.country),
                count: r.count,
              }))}
              onPick={setCountry}
              empty={t('analytics.noCountryData')}
            />
            <BreakdownCard
              title={t('analytics.byDevice')}
              rows={a.data.byDevice.map((r) => ({
                key: r.device,
                label: t(`analytics.device.${r.device}`, r.device),
                count: r.count,
              }))}
              onPick={setDevice}
            />
            <BreakdownCard
              title={t('analytics.byBrowser')}
              rows={a.data.byBrowser.map((r) => ({ key: r.browser, label: r.browser, count: r.count }))}
            />
            <BreakdownCard
              title={t('analytics.byType')}
              rows={a.data.byType.map((r) => ({ key: r.type, label: r.type, count: r.count }))}
              onPick={setType}
            />
          </div>

          {a.data.byCampaign.length > 0 ? (
            <Card>
              <CardHeader>
                <CardTitle>{t('analytics.byCampaign')}</CardTitle>
              </CardHeader>
              <CardContent>
                <Table>
                  <THead>
                    <TR>
                      <TH>{t('analytics.utmSource')}</TH>
                      <TH>{t('analytics.utmMedium')}</TH>
                      <TH>{t('analytics.utmCampaign')}</TH>
                      <TH className="text-right">{t('analytics.events')}</TH>
                    </TR>
                  </THead>
                  <TBody>
                    {a.data.byCampaign.map((c) => (
                      <TR key={`${c.source}|${c.medium}|${c.campaign}`}>
                        <TD className="font-medium">{c.source}</TD>
                        <TD className="text-muted-foreground">{c.medium ?? '—'}</TD>
                        <TD className="text-muted-foreground">{c.campaign ?? '—'}</TD>
                        <TD className="text-right tabular-nums">{c.count.toLocaleString()}</TD>
                      </TR>
                    ))}
                  </TBody>
                </Table>
              </CardContent>
            </Card>
          ) : null}
        </div>
      )}

      <ConfirmDeleteDialog
        open={clearOpen}
        onOpenChange={setClearOpen}
        title={t('analytics.clearTitle')}
        description={t('analytics.clearDescription')}
        consequences={[
          t('analytics.clearConsequenceEvents'),
          t('analytics.clearConsequenceRollups'),
          t('analytics.clearConsequenceIrreversible'),
        ]}
        confirmationValue={t('analytics.clearConfirmWord')}
        confirmLabel={t('analytics.clear')}
        pending={clear.isPending}
        onConfirm={() => clear.mutate()}
      />
    </Page>
  );
}

function canClear(me: MyPermissions | undefined): boolean {
  return can(me, Perm.TenantSettings);
}

function FilterSelect({
  value,
  onChange,
  placeholder,
  options,
  label,
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder: string;
  options: string[];
  label?: (v: string) => string;
}) {
  // Nothing to filter by means the dropdown would only ever offer "any".
  if (options.length === 0) return null;
  return (
    <Select value={value} onValueChange={onChange}>
      <SelectTrigger className="h-9 w-40">
        <SelectValue placeholder={placeholder} />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={ANY}>{placeholder}</SelectItem>
        {options.map((o) => (
          <SelectItem key={o} value={o}>
            {label ? label(o) : o}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

/**
 * A ranked breakdown with a share bar. Rows are clickable when `onPick` is given,
 * which is how a reader drills from "most traffic is mobile" into just that slice.
 */
function BreakdownCard({
  title,
  rows,
  onPick,
  empty,
}: {
  title: string;
  rows: { key: string; label: string; count: number }[];
  onPick?: (key: string) => void;
  empty?: string;
}) {
  const max = rows.reduce((m, r) => Math.max(m, r.count), 0);
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
      </CardHeader>
      <CardContent>
        {rows.length === 0 ? (
          <p className="text-sm text-muted-foreground">{empty ?? '—'}</p>
        ) : (
          <ul className="space-y-1">
            {rows.map((r) => (
              <li key={r.key}>
                <button
                  type="button"
                  disabled={!onPick}
                  onClick={() => onPick?.(r.key)}
                  className="relative flex w-full items-center justify-between gap-3 rounded-md px-2 py-1.5 text-sm enabled:hover:bg-accent/50 disabled:cursor-default"
                >
                  {/* Share bar behind the label rather than a separate column: it
                      reads as proportion at a glance without stealing width. */}
                  <span
                    aria-hidden
                    className="absolute inset-y-0 left-0 rounded-md bg-primary/10"
                    style={{ width: `${max > 0 ? (r.count / max) * 100 : 0}%` }}
                  />
                  <span className="relative min-w-0 truncate text-left" title={r.label}>
                    {r.label}
                  </span>
                  <span className="relative shrink-0 tabular-nums text-muted-foreground">
                    {r.count.toLocaleString()}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

function StatCard({ label, value }: { label: string; value: number }) {
  return (
    <Card>
      <CardContent className="p-5">
        <p className="text-xs uppercase tracking-wide text-muted-foreground">{label}</p>
        <p className="mt-1 text-2xl font-bold">{value.toLocaleString()}</p>
      </CardContent>
    </Card>
  );
}
