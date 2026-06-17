import { useMutation } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { CheckCircle2, MailCheck, XCircle } from 'lucide-react';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Page } from '../../components/Page';
import { Button } from '../../components/ui/button';
import { Card, CardContent } from '../../components/ui/card';
import { CenteredSpinner } from '../../components/ui/spinner';
import { api } from '../../lib/api';

export function InviteAcceptPage() {
  const { t } = useTranslation();
  const token = new URLSearchParams(window.location.search).get('token') ?? '';

  const accept = useMutation({
    mutationFn: () => api.post<{ tenantId: string }>('/admin/invitations/accept', { token }),
  });

  useEffect(() => {
    if (token) accept.mutate();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Page className="max-w-md">
      <Card className="mt-10">
        <CardContent className="flex flex-col items-center gap-4 py-10 text-center">
          {accept.isPending ? (
            <CenteredSpinner />
          ) : accept.isSuccess ? (
            <>
              <CheckCircle2 className="h-10 w-10 text-[hsl(var(--success))]" />
              <p className="font-medium">{t('members.inviteSent')}</p>
              <Link to="/">
                <Button>{t('actions.open')}</Button>
              </Link>
            </>
          ) : (
            <>
              {token ? <XCircle className="h-10 w-10 text-destructive" /> : <MailCheck className="h-10 w-10 text-muted-foreground" />}
              <p className="text-sm text-muted-foreground">
                {token ? t('errors.generic') : t('members.invite')}
              </p>
            </>
          )}
        </CardContent>
      </Card>
    </Page>
  );
}
