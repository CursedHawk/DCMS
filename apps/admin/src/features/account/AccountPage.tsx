import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { KeyRound, Trash2, Lock, GitBranch } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Page, PageHeader } from '../../components/Page';
import { Button } from '../../components/ui/button';
import { Card, CardContent } from '../../components/ui/card';
import { Input } from '../../components/ui/input';
import { Label } from '../../components/ui/label';
import { CenteredSpinner } from '../../components/ui/spinner';
import { accountApi } from './accountApi';
import { DeleteAccountCard } from './DeleteAccountCard';

export function AccountPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const me = useQuery({ queryKey: ['account-me'], queryFn: () => accountApi.me() });
  const keys = useQuery({ queryKey: ['account-ssh-keys'], queryFn: () => accountApi.listKeys() });

  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [keyTitle, setKeyTitle] = useState('');
  const [keyValue, setKeyValue] = useState('');

  const hasPassword = !!me.data?.hasGitPassword;

  const savePassword = useMutation({
    mutationFn: () =>
      accountApi.setPassword({
        currentPassword: hasPassword ? currentPassword : undefined,
        newPassword,
      }),
    onSuccess: () => {
      toast.success(t('account.passwordSaved'));
      setCurrentPassword('');
      setNewPassword('');
      void qc.invalidateQueries({ queryKey: ['account-me'] });
    },
    onError: (e: Error) => toast.error(e.message),
  });

  const addKey = useMutation({
    mutationFn: () => accountApi.addKey({ title: keyTitle, key: keyValue }),
    onSuccess: () => {
      toast.success(t('account.keyAdded'));
      setKeyTitle('');
      setKeyValue('');
      void qc.invalidateQueries({ queryKey: ['account-ssh-keys'] });
    },
    onError: (e: Error) => toast.error(e.message),
  });

  const deleteKey = useMutation({
    mutationFn: (id: number) => accountApi.deleteKey(id),
    onSuccess: () => {
      toast.success(t('account.keyDeleted'));
      void qc.invalidateQueries({ queryKey: ['account-ssh-keys'] });
    },
    onError: (e: Error) => toast.error(e.message),
  });

  if (me.isLoading) return <CenteredSpinner />;

  return (
    <Page>
      <PageHeader title={t('account.title')} description={t('account.subtitle')} />

      <div className="mx-auto grid w-full max-w-2xl gap-6">
        {/* Profile */}
        <Card>
          <CardContent className="space-y-3 p-5">
            <h2 className="text-sm font-semibold">{t('account.profile')}</h2>
            <div className="grid gap-1">
              <Label>{t('account.email')}</Label>
              <Input value={me.data?.email ?? ''} disabled />
            </div>
            {me.data?.forgejoUsername ? (
              <div className="grid gap-1">
                <Label>{t('account.gitUsername')}</Label>
                <Input value={me.data.forgejoUsername} disabled />
                <p className="text-xs text-muted-foreground">{t('account.gitUsernameHint')}</p>
              </div>
            ) : null}
          </CardContent>
        </Card>

        {/* Git password */}
        <Card>
          <CardContent className="space-y-3 p-5">
            <h2 className="flex items-center gap-2 text-sm font-semibold">
              <Lock className="h-4 w-4" /> {t('account.gitPassword')}
            </h2>
            <p className="text-xs text-muted-foreground">
              {hasPassword ? t('account.changePasswordHint') : t('account.setPasswordHint')}
            </p>
            <form
              className="space-y-3"
              onSubmit={(e) => {
                e.preventDefault();
                savePassword.mutate();
              }}
            >
              {hasPassword ? (
                <div className="grid gap-1">
                  <Label htmlFor="cur">{t('account.currentPassword')}</Label>
                  <Input
                    id="cur"
                    type="password"
                    autoComplete="current-password"
                    value={currentPassword}
                    onChange={(e) => setCurrentPassword(e.target.value)}
                  />
                </div>
              ) : null}
              <div className="grid gap-1">
                <Label htmlFor="new">{t('account.newPassword')}</Label>
                <Input
                  id="new"
                  type="password"
                  autoComplete="new-password"
                  minLength={10}
                  value={newPassword}
                  onChange={(e) => setNewPassword(e.target.value)}
                />
              </div>
              <Button type="submit" disabled={savePassword.isPending || newPassword.length < 10}>
                {hasPassword ? t('account.changePassword') : t('account.setPassword')}
              </Button>
            </form>
          </CardContent>
        </Card>

        {/* SSH keys */}
        <Card>
          <CardContent className="space-y-3 p-5">
            <h2 className="flex items-center gap-2 text-sm font-semibold">
              <KeyRound className="h-4 w-4" /> {t('account.sshKeys')}
            </h2>
            <p className="text-xs text-muted-foreground">{t('account.sshKeysHint')}</p>

            {keys.data && keys.data.length > 0 ? (
              <ul className="divide-y rounded-md border">
                {keys.data.map((k) => (
                  <li key={k.id} className="flex items-center gap-3 p-3 text-sm">
                    <KeyRound className="h-4 w-4 shrink-0 text-muted-foreground" />
                    <div className="min-w-0 flex-1">
                      <p className="truncate font-medium">{k.title}</p>
                      <p className="truncate font-mono text-xs text-muted-foreground">{k.fingerprint}</p>
                    </div>
                    <Button
                      variant="ghost"
                      size="icon"
                      aria-label={t('common.delete')}
                      onClick={() => deleteKey.mutate(k.id)}
                      disabled={deleteKey.isPending}
                    >
                      <Trash2 className="h-4 w-4 text-destructive" />
                    </Button>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="text-sm text-muted-foreground">{t('account.noKeys')}</p>
            )}

            <form
              className="space-y-3 border-t pt-3"
              onSubmit={(e) => {
                e.preventDefault();
                addKey.mutate();
              }}
            >
              <div className="grid gap-1">
                <Label htmlFor="kt">{t('account.keyTitle')}</Label>
                <Input id="kt" value={keyTitle} onChange={(e) => setKeyTitle(e.target.value)} placeholder="laptop" />
              </div>
              <div className="grid gap-1">
                <Label htmlFor="kv">{t('account.keyValue')}</Label>
                <textarea
                  id="kv"
                  value={keyValue}
                  onChange={(e) => setKeyValue(e.target.value)}
                  placeholder="ssh-ed25519 AAAA…"
                  rows={3}
                  className="w-full rounded-md border bg-background px-3 py-2 font-mono text-xs"
                />
              </div>
              <Button type="submit" disabled={addKey.isPending || !keyValue.trim()}>
                {t('account.addKey')}
              </Button>
            </form>
          </CardContent>
        </Card>

        {/* Danger zone */}
        {me.data?.email ? <DeleteAccountCard email={me.data.email} /> : null}

        {/* Git help */}
        <Card>
          <CardContent className="space-y-2 p-5">
            <h2 className="flex items-center gap-2 text-sm font-semibold">
              <GitBranch className="h-4 w-4" /> {t('account.gitAccess')}
            </h2>
            <p className="text-xs text-muted-foreground">{t('account.gitAccessHint')}</p>
          </CardContent>
        </Card>
      </div>
    </Page>
  );
}
