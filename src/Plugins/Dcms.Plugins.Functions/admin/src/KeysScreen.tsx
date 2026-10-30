import { useMemo, useState } from 'react';
import { Plus } from 'lucide-react';
import {
  Button, Callout, CopyButton, DataTable, Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, DialogTitle,
  Input, Label, LoadFailed, toastApiError, useConfirm, type Column,
} from '@dcms/ui';
import { usePluginT, type PluginScreenProps } from '@dcms/plugin-ui';
import { useKeyActions, useKeys, type ApiKeyInfo } from './api';

/** Keys outside callers send in X-Api-Key to functions whose access names "apiKey". */
export function KeysScreen({ instance }: PluginScreenProps) {
  const slug = instance!.slug;
  const { t, i18n } = usePluginT();
  const confirm = useConfirm();
  const keys = useKeys(slug);
  const { create, revoke } = useKeyActions(slug);
  const [adding, setAdding] = useState(false);
  const [name, setName] = useState('');
  const [functions, setFunctions] = useState('');
  const [made, setMade] = useState<string | null>(null);

  const columns = useMemo<Column<ApiKeyInfo>[]>(() => [
    { id: 'name', header: t('keys.name'), primary: true, cell: (k) => k.name },
    { id: 'prefix', header: t('keys.prefix'), cell: (k) => <span className="font-mono text-xs">{k.prefix}…</span> },
    { id: 'functions', header: t('keys.functions'), cell: (k) => (k.functions.length ? k.functions.join(', ') : t('keys.allFunctions')) },
    { id: 'created', header: t('keys.created'), cell: (k) => new Date(k.createdAt).toLocaleString(i18n.language) },
    {
      id: 'actions', header: '', align: 'right', hideOnCard: true,
      cell: (k) => (
        <Button size="sm" variant="outline" onClick={async () => {
          if (await confirm({ title: t('keys.revokeTitle', { name: k.name }), description: t('keys.revokeHint'), confirmLabel: t('keys.revoke'), destructive: true })) {
            revoke.mutate(k.id, { onError: (e) => toastApiError(e, t) });
          }
        }}>{t('keys.revoke')}</Button>
      ),
    },
  ], [t, i18n.language, confirm, revoke]);

  const close = () => { setAdding(false); setMade(null); setName(''); setFunctions(''); };

  return (
    <div className="space-y-4 pt-2">
      <div className="flex items-center justify-between gap-2">
        <p className="text-sm text-muted-foreground">{t('keys.hint')}</p>
        <Button onClick={() => setAdding(true)}><Plus className="h-4 w-4" /> {t('keys.add')}</Button>
      </div>
      <DataTable rows={keys.data ?? []} columns={columns} rowKey={(k) => k.id} isLoading={keys.isLoading}
        error={keys.isError ? <LoadFailed title={t('common.loadFailed')} retryLabel={t('common.retry')} onRetry={() => void keys.refetch()} /> : undefined}
        empty={t('keys.empty')} />
      <Dialog open={adding} onOpenChange={(o) => !o && close()}>
        <DialogContent>
          <DialogHeader><DialogTitle>{made ? t('keys.madeTitle') : t('keys.add')}</DialogTitle></DialogHeader>
          <DialogBody className="space-y-3">
            {made ? (
              <>
                <Callout tone="warning">{t('keys.shownOnce')}</Callout>
                <div className="flex items-center gap-2">
                  <code className="min-w-0 flex-1 break-all rounded-md bg-muted p-2 text-xs">{made}</code>
                  <CopyButton value={made} />
                </div>
              </>
            ) : (
              <>
                <div className="space-y-1">
                  <Label htmlFor="key-name">{t('keys.name')}</Label>
                  <Input id="key-name" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
                </div>
                <div className="space-y-1">
                  <Label htmlFor="key-functions">{t('keys.functions')}</Label>
                  <Input id="key-functions" value={functions} placeholder={t('keys.functionsHint')} onChange={(e) => setFunctions(e.target.value)} />
                </div>
              </>
            )}
          </DialogBody>
          <DialogFooter>
            {made ? (
              <Button onClick={close}>{t('keys.done')}</Button>
            ) : (
              <Button disabled={!name.trim() || create.isPending} onClick={() => create.mutate(
                { name: name.trim(), functions: functions.split(',').map((f) => f.trim()).filter(Boolean) },
                { onSuccess: (r) => setMade(r.secret), onError: (e) => toastApiError(e, t) },
              )}>{t('keys.create')}</Button>
            )}
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
