import { useState } from 'react';
import { Button, Input, Label, LoadFailed, toast, toastApiError, useConfirm } from '@dcms/ui';
import { usePluginT, type PluginScreenProps } from '@dcms/plugin-ui';
import { useSecretActions, useSecrets } from './api';

const NAME = /^[A-Z_][A-Z0-9_]{0,63}$/;

/** Values functions read with secrets.get: set or replaced here, never shown again. */
export function SecretsScreen({ instance }: PluginScreenProps) {
  const slug = instance!.slug;
  const { t } = usePluginT();
  const confirm = useConfirm();
  const secrets = useSecrets(slug);
  const { set, remove } = useSecretActions(slug);
  const [name, setName] = useState('');
  const [value, setValue] = useState('');

  if (secrets.isError) {
    return <LoadFailed title={t('common.loadFailed')} retryLabel={t('common.retry')} onRetry={() => void secrets.refetch()} />;
  }
  return (
    <div className="space-y-6 pt-2">
      <p className="text-sm text-muted-foreground">{t('secrets.hint')}</p>
      <ul className="divide-y rounded-lg border">
        {(secrets.data ?? []).length === 0 ? <li className="p-3 text-sm text-muted-foreground">{t('secrets.empty')}</li> : null}
        {(secrets.data ?? []).map((s) => (
          <li key={s} className="flex items-center justify-between gap-2 p-3">
            <span className="font-mono text-sm">{s}</span>
            <div className="flex gap-2">
              <Button size="sm" variant="outline" onClick={() => { setName(s); setValue(''); }}>{t('secrets.replace')}</Button>
              <Button size="sm" variant="outline" onClick={async () => {
                if (await confirm({ title: t('secrets.deleteTitle', { name: s }), description: t('secrets.deleteHint'), confirmLabel: t('secrets.delete'), destructive: true })) {
                  remove.mutate(s, { onError: (e) => toastApiError(e, t) });
                }
              }}>{t('secrets.delete')}</Button>
            </div>
          </li>
        ))}
      </ul>
      <form className="grid max-w-xl gap-3" onSubmit={(e) => {
        e.preventDefault();
        set.mutate({ name, value }, {
          onSuccess: () => { toast.success(t('secrets.saved', { name })); setName(''); setValue(''); },
          onError: (err) => toastApiError(err, t),
        });
      }}>
        <div className="space-y-1">
          <Label htmlFor="secret-name">{t('secrets.name')}</Label>
          <Input id="secret-name" value={name} placeholder="STRIPE_KEY" onChange={(e) => setName(e.target.value.toUpperCase())} />
        </div>
        <div className="space-y-1">
          <Label htmlFor="secret-value">{t('secrets.value')}</Label>
          <Input id="secret-value" type="password" autoComplete="off" value={value} onChange={(e) => setValue(e.target.value)} />
        </div>
        <div>
          <Button type="submit" disabled={!NAME.test(name) || !value || set.isPending}>{t('secrets.save')}</Button>
        </div>
      </form>
    </div>
  );
}
