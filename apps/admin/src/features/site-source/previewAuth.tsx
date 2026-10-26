import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Check, LogIn, LogOut, UserRound, Users } from 'lucide-react';
import { useCallback, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { csrfHeader } from '@dcms/core';
import {
  Button,
  CenteredSpinner,
  Dialog,
  DialogBody,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  Input,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { usePluginUi } from '../../pluginHost/api';
import { getCurrentTenantSlug } from '../../tenants';

/** Who a site builder's preview is signed in as (#269), from the edge's preview endpoints. */
export interface PreviewIdentity {
  /** False when the workspace has no site sign-in (User Authentication is off). */
  available: boolean;
  signedIn: boolean;
  name: string | null;
  email: string | null;
  groups: string[];
  /** "View as": an administrator's short-lived stand-in, not the user's own sign-in. */
  viewAs: boolean;
}

/** The message the edge's done page posts to this window when a popup sign-in or sign-out finishes. */
const DONE_MESSAGE = 'dcms:preview-auth';

/** The edge's preview endpoints for one site: where a preview asks who it is and what it may open. */
export function previewEdgeBase(siteId: string): string {
  return `/.edge/preview/${encodeURIComponent(getCurrentTenantSlug() ?? '')}/${siteId}`;
}

/**
 * A preview's site sign-in: who it is, signing in and out (in a popup, so the builder keeps its
 * state), and "View as". `version` changes whenever that changes — a preview keys on it to draw
 * again as the new person.
 */
export function usePreviewAuth(siteId: string | undefined) {
  const qc = useQueryClient();
  const base = siteId ? previewEdgeBase(siteId) : null;
  const [version, setVersion] = useState(0);

  const me = useQuery({
    queryKey: ['preview-auth', base],
    enabled: !!base,
    staleTime: 30_000,
    // Not behind the edge (local development), or no rights to the site: no preview sign-in.
    queryFn: async (): Promise<PreviewIdentity | null> => {
      const res = await fetch(`${base}/me`, { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      return res.ok ? ((await res.json()) as PreviewIdentity) : null;
    },
  });

  const changed = useCallback(() => {
    void qc.invalidateQueries({ queryKey: ['preview-auth'] });
    setVersion((v) => v + 1);
  }, [qc]);

  useEffect(() => {
    const onMessage = (event: MessageEvent) => {
      if (event.origin === window.location.origin && (event.data as { type?: string } | null)?.type === DONE_MESSAGE) changed();
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, [changed]);

  // Stable, because the IDE preview re-attaches its bridge when they change.
  const popup = useCallback(
    (action: 'signin' | 'signout') => {
      if (base) window.open(`${base}/${action}`, 'dcms-preview-auth', 'popup,width=480,height=680');
    },
    [base],
  );
  const signIn = useCallback(() => popup('signin'), [popup]);
  const signOut = useCallback(() => popup('signout'), [popup]);

  const viewAs = useMutation({
    mutationFn: async ({ instance, userId }: { instance: string; userId: string }) => {
      const res = await fetch(`${base}/view-as`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'Idempotency-Key': crypto.randomUUID(), ...csrfHeader() },
        body: JSON.stringify({ instance, userId }),
      });
      if (!res.ok) {
        const body = (await res.json().catch(() => null)) as { error?: string } | null;
        throw new Error(body?.error ?? `${res.status}`);
      }
    },
    onSuccess: changed,
  });

  return { base, identity: me.data ?? null, version, signIn, signOut, viewAs };
}

export type PreviewAuth = ReturnType<typeof usePreviewAuth>;

/**
 * The visitor a preview is signed in as (Visitor Authentication). The page's own sign-in and
 * registration forms make real visitors of this environment (ADR 0026: a preview acts on its
 * environment's live data), so there is no "View as" here — register one, preferably in a
 * development environment. Nothing when no instance is switched on.
 */
export function PreviewVisitorMenu({
  visitor,
  onSignOut,
}: {
  visitor: { name?: string | null; email?: string | null } | null | undefined;
  onSignOut: () => void;
}) {
  const { t } = useTranslation();
  const ui = usePluginUi();
  const installed = !!ui.data?.find((p) => p.pluginId === 'visitor-auth')?.instances.some((i) => i.enabled);
  if (!installed) return null;
  const label = visitor ? t('previewAuth.visitorAs', { name: visitor.name || visitor.email || '' }) : t('previewAuth.noVisitor');
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="sm" className="h-6 min-w-0 gap-1 px-2 text-xs" aria-label={t('previewAuth.visitorMenu')}>
          <UserRound className="h-3.5 w-3.5 shrink-0" />
          <span className="min-w-0 truncate">{label}</span>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuLabel className="max-w-64 text-xs font-normal text-muted-foreground">{t('previewAuth.visitorHint')}</DropdownMenuLabel>
        <DropdownMenuSeparator />
        {visitor ? (
          <DropdownMenuItem onSelect={onSignOut}>
            <LogOut className="h-4 w-4" /> {t('previewAuth.visitorSignOut')}
          </DropdownMenuItem>
        ) : null}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** The preview toolbar's "who am I on the site" control. Nothing when the workspace has no site sign-in. */
export function PreviewIdentityMenu({ auth }: { auth: PreviewAuth }) {
  const { t } = useTranslation();
  const [picking, setPicking] = useState(false);
  const who = auth.identity;
  if (!who?.available) return null;

  const label = !who.signedIn
    ? t('previewAuth.signedOut')
    : t(who.viewAs ? 'previewAuth.viewingAs' : 'previewAuth.signedInAs', { name: who.name ?? who.email ?? '' });

  return (
    <>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="outline" size="sm" className="h-6 min-w-0 gap-1 px-2 text-xs" aria-label={t('previewAuth.menu')}>
            <UserRound className="h-3.5 w-3.5 shrink-0" />
            <span className="min-w-0 truncate">{label}</span>
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuLabel className="max-w-64 text-xs font-normal text-muted-foreground">{t('previewAuth.hint')}</DropdownMenuLabel>
          <DropdownMenuSeparator />
          <DropdownMenuItem onSelect={auth.signIn}>
            <LogIn className="h-4 w-4" /> {t('previewAuth.signIn')}
          </DropdownMenuItem>
          <DropdownMenuItem onSelect={() => setPicking(true)}>
            <Users className="h-4 w-4" /> {t('previewAuth.viewAs')}
          </DropdownMenuItem>
          {who.signedIn ? (
            <DropdownMenuItem onSelect={auth.signOut}>
              <LogOut className="h-4 w-4" /> {t('previewAuth.signOut')}
            </DropdownMenuItem>
          ) : null}
        </DropdownMenuContent>
      </DropdownMenu>
      {picking ? <ViewAsDialog auth={auth} onClose={() => setPicking(false)} /> : null}
    </>
  );
}

interface SiteUserRow {
  id: string;
  email: string;
  displayName: string | null;
  status: string;
}

/** Pick a site user to preview as, from the User Authentication directory. */
function ViewAsDialog({ auth, onClose }: { auth: PreviewAuth; onClose: () => void }) {
  const { t } = useTranslation();
  const [search, setSearch] = useState('');
  const ui = usePluginUi();
  const instance = ui.data?.find((p) => p.pluginId === 'user-auth')?.instances.find((i) => i.enabled)?.slug;
  const users = useQuery({
    queryKey: ['preview-auth-users', instance, search],
    enabled: !!instance,
    queryFn: () =>
      api.get<{ items: SiteUserRow[] }>(
        `/admin/plugins/${instance}/users?pageSize=20${search.trim() ? `&search=${encodeURIComponent(search.trim())}` : ''}`,
      ),
  });

  const pick = (user: SiteUserRow) =>
    auth.viewAs.mutate(
      { instance: instance!, userId: user.id },
      {
        onSuccess: () => {
          toast.success(t('previewAuth.viewingAs', { name: user.displayName ?? user.email }));
          onClose();
        },
        onError: (e) => toast.error(t('previewAuth.viewAsFailed', { reason: e.message })),
      },
    );

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t('previewAuth.viewAsTitle')}</DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-3">
          <p className="text-sm text-muted-foreground">{t('previewAuth.viewAsHint')}</p>
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder={`${t('actions.search')}…`}
            aria-label={t('actions.search')}
          />
          {users.isLoading ? (
            <CenteredSpinner />
          ) : users.isError ? (
            <p className="text-sm text-destructive">{t('previewAuth.usersFailed')}</p>
          ) : (
            <ul className="max-h-72 space-y-1 overflow-y-auto">
              {(users.data?.items ?? []).map((u) => {
                const active = u.status === 'active';
                return (
                  <li key={u.id}>
                    <button
                      type="button"
                      disabled={!active || auth.viewAs.isPending}
                      onClick={() => pick(u)}
                      className="flex w-full min-w-0 items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-accent disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      <span className="min-w-0 flex-1 [overflow-wrap:anywhere]">
                        {u.displayName ?? u.email}
                        {u.displayName ? <span className="block text-xs text-muted-foreground">{u.email}</span> : null}
                      </span>
                      {active ? (
                        auth.identity?.email === u.email ? <Check className="h-4 w-4 shrink-0" aria-hidden /> : null
                      ) : (
                        <span className="shrink-0 text-xs text-muted-foreground">{t('previewAuth.cannotSignIn')}</span>
                      )}
                    </button>
                  </li>
                );
              })}
              {users.data?.items.length === 0 ? (
                <li className="px-2 py-4 text-center text-sm text-muted-foreground">{t('common.noResults')}</li>
              ) : null}
            </ul>
          )}
        </DialogBody>
      </DialogContent>
    </Dialog>
  );
}
