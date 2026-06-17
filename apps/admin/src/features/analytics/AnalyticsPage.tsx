import { useQuery } from '@tanstack/react-query';
import { TrendingUp } from 'lucide-react';
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
import { Page, PageHeader } from '../../components/Page';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { EmptyState } from '../../components/ui/empty-state';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '../../components/ui/select';
import { CenteredSpinner } from '../../components/ui/spinner';
import { TBody, TD, TH, THead, TR, Table } from '../../components/ui/table';
import { api } from '../../lib/api';

interface Analytics {
  total: number;
  byDay: { day: string; count: number }[];
  topPaths: { path: string; count: number }[];
  byType: { type: string; count: number }[];
}

export function AnalyticsPage() {
  const { t } = useTranslation();
  const [days, setDays] = useState('30');
  const a = useQuery({
    queryKey: ['analytics', days],
    queryFn: () => api.get<Analytics>(`/admin/analytics?days=${days}`),
  });

  return (
    <Page>
      <PageHeader
        title={t('analytics.title')}
        actions={
          <Select value={days} onValueChange={setDays}>
            <SelectTrigger className="w-40">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {['7', '30', '90'].map((d) => (
                <SelectItem key={d} value={d}>
                  {t('analytics.days', { count: Number(d) })}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        }
      />

      {a.isLoading ? (
        <CenteredSpinner />
      ) : !a.data || a.data.total === 0 ? (
        <EmptyState icon={TrendingUp} title={t('analytics.title')} description={t('common.noResults')} />
      ) : (
        <div className="space-y-6">
          <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
            <StatCard label={t('analytics.pageviews')} value={a.data.total} />
            {a.data.byType.slice(0, 3).map((x) => (
              <StatCard key={x.type} label={x.type} value={x.count} />
            ))}
          </div>

          <Card>
            <CardHeader>
              <CardTitle>{t('analytics.pageviews')}</CardTitle>
            </CardHeader>
            <CardContent>
              <div className="h-72 w-full">
                <ResponsiveContainer width="100%" height="100%">
                  <AreaChart data={a.data.byDay} margin={{ left: -20, right: 8, top: 8 }}>
                    <defs>
                      <linearGradient id="ev" x1="0" y1="0" x2="0" y2="1">
                        <stop offset="0%" stopColor="#6366f1" stopOpacity={0.35} />
                        <stop offset="100%" stopColor="#6366f1" stopOpacity={0} />
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
                    <Area type="monotone" dataKey="count" stroke="#6366f1" strokeWidth={2} fill="url(#ev)" />
                  </AreaChart>
                </ResponsiveContainer>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t('analytics.topPaths')}</CardTitle>
            </CardHeader>
            <CardContent>
              <Table>
                <THead>
                  <TR>
                    <TH>Path</TH>
                    <TH className="text-right">{t('analytics.pageviews')}</TH>
                  </TR>
                </THead>
                <TBody>
                  {a.data.topPaths.map((p) => (
                    <TR key={p.path}>
                      <TD className="font-medium">{p.path}</TD>
                      <TD className="text-right">{p.count}</TD>
                    </TR>
                  ))}
                </TBody>
              </Table>
            </CardContent>
          </Card>
        </div>
      )}
    </Page>
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
