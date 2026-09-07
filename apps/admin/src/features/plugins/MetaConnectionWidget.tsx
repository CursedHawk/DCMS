import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, Check, Instagram, Facebook, Link2, RefreshCw, Unlink } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import type { WidgetProps } from '@rjsf/utils';
import { Badge, Button, cn, toastApiError } from '@dcms/ui';
import { api } from '../../lib/api';
export type MetaProvider = 'Facebook' | 'InstagramLogin';

export interface MetaConnection {
  id: string;
  provider: MetaProvider;
  accountName: string;
  accountUsername?: string | null;
  avatarUrl?: string | null;
  status: 'Active' | 'NeedsReauth' | 'Revoked';
  supportsStories: boolean;
  tokenExpiresAt?: string | null;
  lastError?: string | null;
  connectedAt: string;
}

export function useMetaConnections() {
  return useQuery({
    queryKey: ['social', 'connections'],
    queryFn: () => api.get<MetaConnection[]>('/admin/social/connections'),
  });
}

/**
 * RJSF widget for schema fields declared with `"format": "meta-connection"` — the
 * Instagram and Facebook plugins' `connectionId`.
 *
 * A connection cannot be typed into a form: it is the result of an OAuth round trip
 * to Meta. So this renders the accounts the tenant has already connected as a
 * picker, plus the button that starts consent for a new one — the same extension
 * point the media widget uses, for the same reason.
 */
export function MetaConnectionWidget({ value, onChange, registry }: WidgetProps) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const connections = useMetaConnections();

  // Supplied by the edit dialog through formContext. Absent while an instance is being
  // created, which is correct: there is nothing to sync until it exists.
  const instanceId = (registry?.formContext as { instanceId?: string } | undefined)?.instanceId;

  const connect = useMutation({
    mutationFn: (provider: 'facebook' | 'instagramlogin') =>
      api.get<{ authorizeUrl: string }>(`/admin/social/${provider}/connect`),
    onSuccess: ({ authorizeUrl }) => {
      // A full navigation, not a fetch: consent happens on Meta's own origin and
      // ends with a redirect back to the callback.
      window.location.assign(authorizeUrl);
    },
    onError: (e) => toastApiError(e, t, t('social.connectFailed', 'Could not start the Meta connection.')),
  });

  const disconnect = useMutation({
    mutationFn: (id: string) => api.post(`/admin/social/connections/${id}/disconnect`),
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['social', 'connections'] });
      toast.success(t('social.disconnected', 'Account disconnected.'));
    },
    onError: (e) => toastApiError(e, t, t('social.disconnectFailed', 'Could not disconnect the account.')),
  });

  const sync = useMutation({
    mutationFn: () =>
      api.post<{ created: number; updated: number; trimmed: number }>(
        `/admin/social/instances/${instanceId}/sync`,
      ),
    onSuccess: ({ created, updated, trimmed }) => {
      // The counts, not just "done". A sync that pulled nothing looks identical to a
      // broken one otherwise, and "0 new" is the answer to most of the questions an
      // admin presses this button to ask.
      toast.success(
        t('social.syncDone', 'Synced: {{created}} new, {{updated}} updated, {{trimmed}} removed.', {
          created,
          updated,
          trimmed,
        }),
      );
    },
    onError: (e) => toastApiError(e, t, t('social.syncFailed', 'The sync could not be run.')),
  });

  const available = (connections.data ?? []).filter((c) => c.status !== 'Revoked');
  const selected = available.find((c) => c.id === value);

  return (
    <div className="space-y-3">
      {available.length === 0 && !connections.isLoading && (
        <p className="text-sm text-muted-foreground">
          {t(
            'social.noAccounts',
            'No Meta accounts connected yet. Connect one to start pulling in posts.',
          )}
        </p>
      )}

      <ul className="space-y-2">
        {available.map((c) => (
          <li key={c.id}>
            {/* The row and the disconnect control are siblings, not nested. A <button>
                inside a <button> is invalid HTML — browsers may hoist the inner one out
                of the outer, and either way the two are indistinguishable to a keyboard. */}
            <div
              className={cn(
                'flex w-full items-center gap-3 rounded-md border transition-colors',
                c.id === value ? 'border-primary bg-accent' : 'hover:bg-accent/50',
              )}
            >
            <button
              type="button"
              aria-pressed={c.id === value}
              onClick={() => onChange(c.id === value ? undefined : c.id)}
              className="flex min-w-0 flex-1 items-center gap-3 p-3 text-left"
            >
              {c.avatarUrl ? (
                <img src={c.avatarUrl} alt="" className="size-8 rounded-full object-cover" />
              ) : c.provider === 'Facebook' ? (
                <Facebook className="size-5 text-muted-foreground" />
              ) : (
                <Instagram className="size-5 text-muted-foreground" />
              )}

              <span className="min-w-0 flex-1">
                <span className="block truncate font-medium">{c.accountName}</span>
                {c.accountUsername && (
                  <span className="block truncate text-xs text-muted-foreground">
                    @{c.accountUsername}
                  </span>
                )}
              </span>

              {c.status === 'NeedsReauth' && (
                <Badge tone="destructive" className="gap-1">
                  <AlertTriangle className="size-3" />
                  {t('social.needsReauth', 'Reconnect')}
                </Badge>
              )}
              {c.id === value && c.status === 'Active' && <Check className="size-4 text-primary" />}
            </button>

            <div className="flex shrink-0 items-center gap-1 pr-2">
              {c.id === value && instanceId && (
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  disabled={sync.isPending || c.status !== 'Active'}
                  aria-label={t('social.syncNow', 'Sync now')}
                  title={t('social.syncNow', 'Sync now')}
                  onClick={() => sync.mutate()}
                >
                  <RefreshCw className={cn('size-4', sync.isPending && 'animate-spin')} />
                </Button>
              )}
              <Button
                type="button"
                variant="ghost"
                size="sm"
                aria-label={t('social.disconnect', 'Disconnect')}
                onClick={() => disconnect.mutate(c.id)}
              >
                <Unlink className="size-4" />
              </Button>
            </div>
            </div>

            {/* Stories are only readable through a Facebook Page. Saying so where the
                account is chosen is the only place it is actionable — by the time the
                stories toggle does nothing, the admin has no idea why. */}
            {c.id === value && !c.supportsStories && (
              <p className="mt-1 pl-3 text-xs text-muted-foreground">
                {t(
                  'social.noStories',
                  'Stories are not available for this account: Instagram only exposes them for accounts linked to a Facebook Page.',
                )}
              </p>
            )}
            {c.id === value && c.status === 'NeedsReauth' && c.lastError && (
              <p className="mt-1 pl-3 text-xs text-destructive">{c.lastError}</p>
            )}
          </li>
        ))}
      </ul>

      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={connect.isPending}
          onClick={() => connect.mutate('facebook')}
        >
          <Link2 className="size-4" />
          {t('social.connectFacebook', 'Connect Facebook / Instagram')}
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={connect.isPending}
          onClick={() => connect.mutate('instagramlogin')}
        >
          <Instagram className="size-4" />
          {t('social.connectInstagram', 'Connect Instagram only')}
        </Button>
      </div>

      {selected?.tokenExpiresAt && (
        <p className="text-xs text-muted-foreground">
          {t('social.tokenExpires', 'Access renews automatically before {{date}}.', {
            date: new Date(selected.tokenExpiresAt).toLocaleDateString(),
          })}
        </p>
      )}
    </div>
  );
}
