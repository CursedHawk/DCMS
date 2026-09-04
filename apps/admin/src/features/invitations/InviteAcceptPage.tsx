import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { CheckCircle2, MailCheck, XCircle } from 'lucide-react';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Card, CardContent, CenteredSpinner, Page } from '@dcms/admin-ui';
import { ApiError, api } from '../../lib/api';
import { setCurrentTenantSlug } from '../../tenants';

interface AcceptResult {
  tenantId: string;
  membershipId: string;
  tenantSlug?: string | null;
  tenantName?: string | null;
}

export function InviteAcceptPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const token = new URLSearchParams(window.location.search).get('token') ?? '';

  const accept = useMutation({
    mutationFn: () => api.post<AcceptResult>('/admin/invitations/accept', { token }),
    // Someone accepting their first invitation has no tenant selected, so the
    // app would drop them on a workspace-less shell right after joining one.
    // Select the tenant they just joined and refresh the switcher.
    onSuccess: async (result) => {
      if (result.tenantSlug) setCurrentTenantSlug(result.tenantSlug);
      await qc.invalidateQueries({ queryKey: ['me-tenants'] });
    },
  });

  useEffect(() => {
    if (token) accept.mutate();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const failureMessage = !token
    ? t('members.inviteNoToken')
    : accept.error instanceof ApiError
      ? accept.error.message
      : t('errors.generic');

  return (
    <Page className="max-w-md">
      <Card className="mt-10">
        <CardContent className="flex flex-col items-center gap-4 py-10 text-center">
          {accept.isPending ? (
            <CenteredSpinner />
          ) : accept.isSuccess ? (
            <>
              <CheckCircle2 className="h-10 w-10 text-[hsl(var(--success))]" />
              <p className="font-medium">
                {accept.data.tenantName
                  ? t('members.inviteAcceptedTo', { tenant: accept.data.tenantName })
                  : t('members.inviteAccepted')}
              </p>
              <Link to="/">
                <Button>{t('actions.open')}</Button>
              </Link>
            </>
          ) : (
            <>
              {token ? (
                <XCircle className="h-10 w-10 text-destructive" />
              ) : (
                <MailCheck className="h-10 w-10 text-muted-foreground" />
              )}
              <p className="text-sm text-muted-foreground">{failureMessage}</p>
            </>
          )}
        </CardContent>
      </Card>
    </Page>
  );
}
