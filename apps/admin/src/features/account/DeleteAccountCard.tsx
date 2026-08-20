import { useMutation, useQuery } from '@tanstack/react-query';
import { LogOut, ShieldAlert } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { ConfirmDeleteDialog } from '../../components/ConfirmDeleteDialog';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Card, CardContent } from '../../components/ui/card';
import { ApiError, api } from '../../lib/api';
import { logout, userManager } from '../../auth';
import { setCurrentTenantSlug } from '../../tenants';
import { accountApi } from './accountApi';

interface WorkspaceStanding {
  tenantId: string;
  slug: string;
  name: string;
  isOwner: boolean;
  /** The only owner — must transfer or delete the workspace before leaving it. */
  isSoleOwner: boolean;
}

interface MyAccount {
  workspaces: WorkspaceStanding[];
  canDelete: boolean;
}

/**
 * Deleting an account spans two services: admin-api detaches the user from every
 * workspace (reversible — they can be re-invited), then identity destroys the login
 * and the mirrored git account (not reversible). Identity refuses while any
 * membership remains, so the order cannot be subverted, and a failure between the
 * two halves leaves the account intact and simply retryable.
 */
export function DeleteAccountCard({ email }: { email: string }) {
  const { t } = useTranslation();
  const [confirmOpen, setConfirmOpen] = useState(false);

  const standing = useQuery({
    queryKey: ['my-account'],
    queryFn: () => api.get<MyAccount>('/admin/me/account'),
  });

  const remove = useMutation({
    mutationFn: async () => {
      await api.del('/admin/me');
      await accountApi.deleteAccount();
    },
    onSuccess: async () => {
      // Nothing in the app is reachable any more; end the session rather than
      // leaving a signed-in shell whose every request 401s. The end-session
      // redirect is attempted first so the identity cookie is cleared too, but the
      // user row it refers to is already gone — if that fails, dropping the local
      // session and reloading is enough to sign them out.
      setCurrentTenantSlug(null);
      toast.success(t('account.deleted'));
      try {
        await logout();
      } catch {
        await userManager.removeUser();
        window.location.assign('/');
      }
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : (e as Error).message),
  });

  const blockers = (standing.data?.workspaces ?? []).filter((w) => w.isSoleOwner);
  const canDelete = standing.data?.canDelete ?? false;

  return (
    <>
      <Card className="border-destructive/40">
        <CardContent className="space-y-3 p-5">
          <h2 className="flex items-center gap-2 text-sm font-semibold text-destructive">
            <ShieldAlert className="h-4 w-4" /> {t('account.deleteTitle')}
          </h2>
          <p className="text-xs text-muted-foreground">{t('account.deleteHint')}</p>

          {blockers.length > 0 ? (
            <div className="space-y-2 rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm">
              <p>{t('account.deleteBlocked')}</p>
              <div className="flex flex-wrap gap-1">
                {blockers.map((w) => (
                  <Badge key={w.tenantId} tone="destructive">
                    {w.name}
                  </Badge>
                ))}
              </div>
              <p className="text-xs text-muted-foreground">{t('account.deleteBlockedHint')}</p>
            </div>
          ) : null}

          {standing.data && standing.data.workspaces.length > 0 && blockers.length === 0 ? (
            <p className="flex items-center gap-2 text-xs text-muted-foreground">
              <LogOut className="h-3.5 w-3.5" />
              {t('account.deleteLeaves', { count: standing.data.workspaces.length })}
            </p>
          ) : null}

          <Button
            variant="destructive"
            disabled={!canDelete || standing.isLoading || remove.isPending}
            onClick={() => setConfirmOpen(true)}
          >
            {t('account.delete')}
          </Button>
        </CardContent>
      </Card>

      <ConfirmDeleteDialog
        open={confirmOpen}
        onOpenChange={setConfirmOpen}
        title={t('account.deleteTitle')}
        description={t('account.deleteDescription')}
        consequences={[
          t('account.deleteConsequenceLogin'),
          t('account.deleteConsequenceGit'),
          t('account.deleteConsequenceWorkspaces', { count: standing.data?.workspaces.length ?? 0 }),
        ]}
        confirmationValue={email}
        confirmLabel={t('account.delete')}
        pending={remove.isPending}
        onConfirm={() => remove.mutate()}
      />
    </>
  );
}
