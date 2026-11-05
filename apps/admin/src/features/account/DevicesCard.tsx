import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { LogOut, Terminal } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { dateTime, relativeTime } from '@dcms/core';
import { Button, Card, CardContent, Spinner } from '@dcms/ui';
import { accountApi } from './accountApi';

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
      </CardContent>
    </Card>
  );
}
