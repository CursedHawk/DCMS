import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, Pencil, Plug, Plus, RefreshCw, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CenteredSpinner,
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  EmptyState,
  Input,
  Label,
  Page,
  PageHeader,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Textarea,
  toastApiError,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { CONNECTIONS_KEY, type ApiConnection } from './api';

/**
 * External API connections (Mode D backlog #124). DCMS calls the API with the key, on a
 * schedule, for the listed operations only, and sites read the stored responses — so the key
 * never reaches a browser or the public API, and visitors never spend the provider's quota.
 */
export function ConnectionsPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const connections = useQuery({ queryKey: CONNECTIONS_KEY, queryFn: () => api.get<ApiConnection[]>('/admin/connections') });
  const [editing, setEditing] = useState<ApiConnection | 'new' | null>(null);

  const refresh = useMutation({
    mutationFn: (slug: string) => api.post<ApiConnection>(`/admin/connections/${slug}/refresh`),
    onSuccess: async (c) => {
      if (c.lastError) toast.warning(t('connections.refreshFailed'));
      else toast.success(t('connections.refreshed'));
      await qc.invalidateQueries({ queryKey: CONNECTIONS_KEY });
    },
    onError: (e) => toastApiError(e, t),
  });
  const remove = useMutation({
    mutationFn: (slug: string) => api.del(`/admin/connections/${slug}`),
    onSuccess: async () => {
      toast.success(t('connections.removed'));
      await qc.invalidateQueries({ queryKey: CONNECTIONS_KEY });
    },
    onError: (e) => toastApiError(e, t),
  });

  return (
    <Page>
      <PageHeader
        title={t('connections.title')}
        description={t('connections.description')}
        actions={
          <Button onClick={() => setEditing('new')}>
            <Plus className="h-4 w-4" /> {t('connections.add')}
          </Button>
        }
      />
      {connections.isLoading ? (
        <CenteredSpinner />
      ) : connections.data?.length ? (
        <div className="space-y-4">
          {connections.data.map((c) => (
            <Card key={c.slug}>
              <CardContent className="space-y-3 p-5">
                <div className="flex flex-wrap items-center gap-2">
                  <Plug className="h-4 w-4 text-muted-foreground" />
                  <span className="font-medium">{c.name}</span>
                  <code className="text-xs text-muted-foreground">{c.slug}</code>
                  {c.lastError ? (
                    <Badge tone="warning">
                      <AlertTriangle className="h-3 w-3" /> {t('connections.failing')}
                    </Badge>
                  ) : c.refreshedAt ? (
                    <Badge tone="success">{t('connections.synced', { when: new Date(c.refreshedAt).toLocaleString() })}</Badge>
                  ) : null}
                  <div className="ml-auto flex gap-1">
                    <Button size="sm" variant="outline" disabled={refresh.isPending} onClick={() => refresh.mutate(c.slug)}>
                      <RefreshCw className="h-4 w-4" /> {t('connections.refresh')}
                    </Button>
                    <Button size="icon" variant="ghost" title={t('actions.edit')} onClick={() => setEditing(c)}>
                      <Pencil className="h-4 w-4" />
                    </Button>
                    <Button
                      size="icon"
                      variant="ghost"
                      title={t('actions.delete')}
                      onClick={() => window.confirm(t('connections.confirmDelete', { name: c.name })) && remove.mutate(c.slug)}
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </div>
                </div>
                <div className="text-sm text-muted-foreground">{c.baseUrl}</div>
                <ul className="flex flex-wrap gap-1">
                  {c.operations.map((op) => (
                    <li key={op}>
                      <code className="rounded bg-muted px-1.5 py-0.5 text-xs">GET {op}</code>
                    </li>
                  ))}
                </ul>
                {c.lastError && <pre className="whitespace-pre-wrap rounded bg-amber-50 p-2 text-xs text-amber-900 dark:bg-amber-950 dark:text-amber-200">{c.lastError}</pre>}
              </CardContent>
            </Card>
          ))}
        </div>
      ) : (
        <EmptyState icon={Plug} title={t('connections.none')} description={t('connections.noneHint')} />
      )}
      {editing && <ConnectionDialog connection={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}

function ConnectionDialog({ connection, onClose }: { connection: ApiConnection | null; onClose: () => void }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const [slug, setSlug] = useState(connection?.slug ?? '');
  const [name, setName] = useState(connection?.name ?? '');
  const [baseUrl, setBaseUrl] = useState(connection?.baseUrl ?? 'https://');
  const [authKind, setAuthKind] = useState(connection?.authKind ?? 'none');
  const [authName, setAuthName] = useState(connection?.authName ?? '');
  const [secret, setSecret] = useState('');
  const [operations, setOperations] = useState((connection?.operations ?? []).join('\n'));
  const [refreshMinutes, setRefreshMinutes] = useState(String(connection?.refreshMinutes ?? 60));

  const save = useMutation({
    mutationFn: () =>
      api.put<ApiConnection>(`/admin/connections/${slug.trim()}`, {
        name,
        baseUrl,
        authKind,
        authName: authKind === 'header' || authKind === 'query' ? authName : null,
        // Empty keeps the stored key: the key itself is never sent back to the console.
        secret: secret || null,
        operations: operations.split('\n').map((o) => o.trim()).filter(Boolean),
        refreshMinutes: Number(refreshMinutes) || 60,
      }),
    onSuccess: async (c) => {
      if (c.lastError) toast.warning(t('connections.savedWithErrors'));
      else toast.success(t('connections.saved'));
      await qc.invalidateQueries({ queryKey: CONNECTIONS_KEY });
      onClose();
    },
    onError: (e) => toastApiError(e, t),
  });

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent wide>
        <DialogHeader>
          <DialogTitle>{connection ? t('connections.edit', { name: connection.name }) : t('connections.add')}</DialogTitle>
        </DialogHeader>
        <form
          className="space-y-3"
          onSubmit={(e) => {
            e.preventDefault();
            save.mutate();
          }}
        >
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div className="space-y-1">
              <Label htmlFor="conn-name">{t('connections.name')}</Label>
              <Input id="conn-name" value={name} onChange={(e) => setName(e.target.value)} required />
            </div>
            <div className="space-y-1">
              <Label htmlFor="conn-slug">{t('connections.slug')}</Label>
              <Input id="conn-slug" value={slug} disabled={!!connection} placeholder="tickets" onChange={(e) => setSlug(e.target.value)} required />
            </div>
          </div>
          <div className="space-y-1">
            <Label htmlFor="conn-url">{t('connections.baseUrl')}</Label>
            <Input id="conn-url" value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} required />
          </div>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div className="space-y-1">
              <Label>{t('connections.auth')}</Label>
              <Select value={authKind} onValueChange={setAuthKind}>
                <SelectTrigger aria-label={t('connections.auth')}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {(['none', 'bearer', 'header', 'query'] as const).map((k) => (
                    <SelectItem key={k} value={k}>
                      {t(`connections.authKinds.${k}`)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            {(authKind === 'header' || authKind === 'query') && (
              <div className="space-y-1">
                <Label htmlFor="conn-auth-name">{t(authKind === 'header' ? 'connections.headerName' : 'connections.queryName')}</Label>
                <Input id="conn-auth-name" value={authName} placeholder={authKind === 'header' ? 'X-Api-Key' : 'api_key'} onChange={(e) => setAuthName(e.target.value)} />
              </div>
            )}
          </div>
          {authKind !== 'none' && (
            <div className="space-y-1">
              <Label htmlFor="conn-secret">{t('connections.key')}</Label>
              <Input
                id="conn-secret"
                type="password"
                autoComplete="off"
                value={secret}
                placeholder={connection?.hasSecret ? t('connections.keyStored') : ''}
                onChange={(e) => setSecret(e.target.value)}
              />
              <p className="text-xs text-muted-foreground">{t('connections.keyHint')}</p>
            </div>
          )}
          <div className="space-y-1">
            <Label htmlFor="conn-ops">{t('connections.operations')}</Label>
            <Textarea id="conn-ops" rows={4} value={operations} placeholder={'/events\n/events?city=brno'} onChange={(e) => setOperations(e.target.value)} />
            <p className="text-xs text-muted-foreground">{t('connections.operationsHint')}</p>
          </div>
          <div className="w-40 space-y-1">
            <Label htmlFor="conn-refresh">{t('connections.refreshMinutes')}</Label>
            <Input id="conn-refresh" type="number" min={15} max={1440} value={refreshMinutes} onChange={(e) => setRefreshMinutes(e.target.value)} />
          </div>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              {t('actions.cancel')}
            </Button>
            <Button type="submit" disabled={save.isPending || !slug.trim()}>
              {t('actions.save')}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
