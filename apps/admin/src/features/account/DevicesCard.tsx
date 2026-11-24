import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Bot, LogOut, Terminal } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { dateTime, relativeTime } from '@dcms/core';
import { Button, Card, CardContent, Spinner } from '@dcms/ui';
import { api } from '../../lib/api';
import { accountApi } from './accountApi';

/** A desktop agent's token (ADR 0032), minted with `dcms agents create` or `dcms link-agent`. */
export interface AgentToken {
  id: string;
  name: string;
  hint: string;
  workspace: string | null;
  workspaceName: string | null;
  environments: string[];
  readOnly: boolean;
  createdAt: string;
  expiresAt: string;
  lastUsedAt: string | null;
}

/** The `dcms` command lines signed in as this user (ADR 0031), and a way to sign each out. */
export function DevicesCard() {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const devices = useQuery({ queryKey: ['account-devices'], queryFn: () => accountApi.listDevices() });
  const revoke = useMutation({
    mutationFn: (id: string) => accountApi.revokeDevice(id),
    onSuccess: () => {
      toast.success(t('account.deviceEnded'));
      void qc.invalidateQueries({ queryKey: ['account-devices'] });
    },
    onError: () => toast.error(t('account.deviceEndFailed')),
  });
  const agents = useQuery({ queryKey: ['account-agent-tokens'], queryFn: () => api.get<AgentToken[]>('/admin/me/agent-tokens') });
  const revokeAgent = useMutation({
    mutationFn: (id: string) => api.del(`/admin/me/agent-tokens/${encodeURIComponent(id)}`),
    onSuccess: () => {
      toast.success(t('account.agentRevoked'));
      void qc.invalidateQueries({ queryKey: ['account-agent-tokens'] });
    },
    onError: () => toast.error(t('account.agentRevokeFailed')),
  });

  return (
    <Card data-testid="account-devices">
      <CardContent className="space-y-3">
        <h2 className="flex items-center gap-2 text-sm font-semibold">
          <Terminal className="h-4 w-4" /> {t('account.devices')}
        </h2>
        <p className="text-xs text-muted-foreground">{t('account.devicesHint')}</p>
        {devices.isLoading ? <Spinner /> : null}
        {devices.isError ? <p className="text-sm text-destructive">{t('account.devicesFailed')}</p> : null}
        {devices.data?.length === 0 ? <p className="text-sm text-muted-foreground">{t('account.noDevices')}</p> : null}
        {devices.data && devices.data.length > 0 ? (
          <ul className="divide-y rounded-md border">
            {devices.data.map((d) => (
              <li key={d.id} className="flex items-center gap-3 p-3 text-sm">
                <Terminal className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
                <div className="min-w-0 flex-1">
                  <p className="truncate font-medium">{t('account.deviceName')}</p>
                  {d.lastUsedAt ? (
                    <p className="truncate text-xs text-muted-foreground">
                      {t('account.sessionLastSeen', { when: relativeTime(d.lastUsedAt, i18n.language) })}
                    </p>
                  ) : null}
                  {d.createdAt ? (
                    <p className="truncate text-xs text-muted-foreground" title={dateTime(d.createdAt, i18n.language)}>
                      {t('account.sessionSignedIn', { when: relativeTime(d.createdAt, i18n.language) })}
                    </p>
                  ) : null}
                </div>
                <Button variant="ghost" size="sm" onClick={() => revoke.mutate(d.id)} disabled={revoke.isPending}>
                  <LogOut className="h-4 w-4" /> {t('account.sessionEnd')}
                </Button>
              </li>
            ))}
          </ul>
        ) : null}

        <h2 className="flex items-center gap-2 pt-2 text-sm font-semibold">
          <Bot className="h-4 w-4" /> {t('account.agents')}
        </h2>
        <p className="text-xs text-muted-foreground">{t('account.agentsHint')}</p>
        {agents.isError ? <p className="text-sm text-destructive">{t('account.agentsFailed')}</p> : null}
        {agents.data?.length === 0 ? <p className="text-sm text-muted-foreground">{t('account.noAgents')}</p> : null}
        {agents.data && agents.data.length > 0 ? (
          <ul className="divide-y rounded-md border" data-testid="account-agent-tokens">
            {agents.data.map((a) => (
              <li key={a.id} className="flex items-center gap-3 p-3 text-sm">
                <Bot className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
                <div className="min-w-0 flex-1">
                  <p className="truncate font-medium">{a.name}</p>
                  <p className="truncate text-xs text-muted-foreground">
                    {t('account.agentScope', {
                      workspace: a.workspaceName ?? a.workspace ?? '?',
                      environments: a.environments.length ? a.environments.join(', ') : t('account.agentAllEnvironments'),
                    })}
                    {a.readOnly ? ` · ${t('account.agentReadOnly')}` : ''}
                  </p>
                  <p className="truncate text-xs text-muted-foreground" title={dateTime(a.expiresAt, i18n.language)}>
                    {a.lastUsedAt
                      ? t('account.sessionLastSeen', { when: relativeTime(a.lastUsedAt, i18n.language) })
                      : t('account.agentNeverUsed')}
                    {' · '}
                    {t('account.agentExpires', { when: relativeTime(a.expiresAt, i18n.language) })}
                  </p>
                </div>
                <Button variant="ghost" size="sm" onClick={() => revokeAgent.mutate(a.id)} disabled={revokeAgent.isPending}>
                  <LogOut className="h-4 w-4" /> {t('account.agentRevoke')}
                </Button>
              </li>
            ))}
          </ul>
        ) : null}
      </CardContent>
    </Card>
  );
}
