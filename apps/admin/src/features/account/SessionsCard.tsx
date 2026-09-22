import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Laptop, LogOut, MonitorSmartphone, Smartphone } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { dateTime, relativeTime } from '@dcms/core';
import { Badge, Button, Card, CardContent, Spinner } from '@dcms/ui';
import { type EdgeSessionSummary, sessionsApi } from './sessionsApi';
import { describeAgent } from './userAgent';

/**
 * Every browser this operator is signed in on, and a way to end any of them.
 *
 * <p>Only possible since ADR 0014: while the console held its own tokens there was nothing
 * server-side to list or revoke — a token in `localStorage` stayed valid until it expired, on a
 * machine nobody could reach. Now the session is a row at the edge, so "sign out that laptop I
 * left at the office" is a delete.</p>
 *
 * <p>The current session is labelled rather than given a button. Ending it here would work, but
 * it leaves a shell whose every request 401s until something reloads — the sign-out in the user
 * menu already does the whole job, including identity's own cookie.</p>
 */
export function SessionsCard() {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();

  const sessions = useQuery({ queryKey: ['edge-sessions'], queryFn: () => sessionsApi.list() });

  const revoke = useMutation({
    mutationFn: (id: string) => sessionsApi.revoke(id),
    onSuccess: () => {
      toast.success(t('account.sessionEnded'));
      void qc.invalidateQueries({ queryKey: ['edge-sessions'] });
    },
    onError: (e: Error) => toast.error(e.message),
  });

  return (
    <Card>
      <CardContent className="space-y-3 p-5">
        <h2 className="flex items-center gap-2 text-sm font-semibold">
          <MonitorSmartphone className="h-4 w-4" /> {t('account.sessions')}
        </h2>
        <p className="text-xs text-muted-foreground">{t('account.sessionsHint')}</p>

        {sessions.isLoading ? <Spinner /> : null}

        {sessions.isError ? (
          <p className="text-sm text-destructive">{t('account.sessionsFailed')}</p>
        ) : null}

        {sessions.data && sessions.data.length > 0 ? (
          <ul className="divide-y rounded-md border">
            {sessions.data.map((session) => (
              <li key={session.id} className="flex items-center gap-3 p-3 text-sm">
                <DeviceIcon userAgent={session.userAgent} />
                <div className="min-w-0 flex-1">
                  <p className="flex items-center gap-2 truncate font-medium">
                    {describeAgent(session.userAgent) || t('account.sessionUnknownDevice')}
                    {session.current ? <Badge>{t('account.sessionCurrent')}</Badge> : null}
                  </p>
                  <p className="truncate text-xs text-muted-foreground">
                    {session.ip ? `${session.ip} · ` : ''}
                    {t('account.sessionLastSeen', { when: relativeTime(session.lastSeenAt, i18n.language) })}
                  </p>
                  <p
                    className="truncate text-xs text-muted-foreground"
                    // The exact instant, for the case the list is being read because something
                    // looks wrong and "2 days ago" is not precise enough to act on.
                    title={dateTime(session.createdAt, i18n.language)}
                  >
                    {t('account.sessionSignedIn', { when: relativeTime(session.createdAt, i18n.language) })}
                  </p>
                </div>
                {session.current ? null : (
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => revoke.mutate(session.id)}
                    disabled={revoke.isPending}
                  >
                    <LogOut className="h-4 w-4" /> {t('account.sessionEnd')}
                  </Button>
                )}
              </li>
            ))}
          </ul>
        ) : null}

        {sessions.data && sessions.data.length === 0 ? (
          // Only reachable if the row behind this very request vanished between the two calls.
          <p className="text-sm text-muted-foreground">{t('account.noSessions')}</p>
        ) : null}
      </CardContent>
    </Card>
  );
}

function DeviceIcon({ userAgent }: { userAgent: EdgeSessionSummary['userAgent'] }) {
  const mobile = /Android|iPhone|iPad|Mobile/.test(userAgent ?? '');
  const Icon = mobile ? Smartphone : Laptop;
  return <Icon className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />;
}
