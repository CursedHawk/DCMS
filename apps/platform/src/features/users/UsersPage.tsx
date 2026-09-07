import { useState } from 'react';
import { Search } from 'lucide-react';
import {
  Button, CenteredSpinner, EmptyState, Input,
  Table, TBody, TD, TH, THead, TR, toastApiError,
} from '@dcms/ui';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { count, date } from '../../lib/format';
import { can, useMe, Perm } from '../../lib/permissions';
import {
  useConfirmEmail, useGrantRole, useRevokeRole, useSetLock, useUsers,
} from './api';

const GLOBAL_ROLES = ['SuperAdmin', 'Support'] as const;

export function UsersPage() {
  const { t } = useTranslation();
  const [search, setSearch] = useState('');
  const [lockedOnly, setLockedOnly] = useState(false);
  const me = useMe(true);
  const users = useUsers(search, lockedOnly);

  const setLock = useSetLock();
  const grant = useGrantRole();
  const revoke = useRevokeRole();
  const confirmEmail = useConfirmEmail();

  const mayWrite = can(me.data, Perm.UsersWrite);
  const mayChangeRoles = can(me.data, Perm.UsersRoles);
  const onError = (e: unknown) => toastApiError(e, t);

  return (
    <div className="mx-auto w-full max-w-5xl px-6 py-8">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold tracking-tight">Users</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Every account on the platform. One identity spans all workspaces; roles here are
          platform-wide, not per workspace.
        </p>
      </header>

      <div className="mb-4 flex flex-wrap items-center gap-2">
        <div className="relative max-w-sm flex-1">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search by email or name"
            aria-label="Search users"
            className="pl-8"
          />
        </div>
        <Button
          variant={lockedOnly ? 'subtle' : 'outline'}
          size="sm"
          aria-pressed={lockedOnly}
          onClick={() => setLockedOnly((v) => !v)}
        >
          Locked out only
        </Button>
        {users.data && (
          <span className="ml-auto text-sm text-muted-foreground">
            {count(users.data.total)} account{users.data.total === 1 ? '' : 's'}
          </span>
        )}
      </div>

      {users.isLoading ? (
        <CenteredSpinner />
      ) : users.data && users.data.items.length === 0 ? (
        <EmptyState
          title={lockedOnly ? 'Nobody is locked out' : 'No account matches that'}
          description={lockedOnly ? undefined : 'Try part of the email address.'}
        />
      ) : (
        <Table>
          <THead>
            <TR>
              <TH>Account</TH>
              <TH>Platform roles</TH>
              <TH>Git</TH>
              <TH>Joined</TH>
              <TH />
            </TR>
          </THead>
          <TBody>
            {users.data?.items.map((u) => {
              const isSelf = u.id === me.data?.userId;
              return (
                <TR key={u.id}>
                  <TD>
                    <div className="flex flex-col">
                      <span className="text-sm">{u.email ?? '—'}</span>
                      <span className="text-xs text-muted-foreground">
                        {u.displayName ?? 'No display name'}
                        {u.lockedOut && ' · Locked out'}
                        {!u.emailConfirmed && ' · Email unconfirmed'}
                      </span>
                    </div>
                  </TD>

                  <TD>
                    <div className="flex flex-wrap gap-1">
                      {GLOBAL_ROLES.map((role) => {
                        const held = u.roles.includes(role);
                        return (
                          <button
                            key={role}
                            type="button"
                            disabled={!mayChangeRoles || grant.isPending || revoke.isPending}
                            aria-pressed={held}
                            title={
                              mayChangeRoles
                                ? held ? `Revoke ${role}` : `Grant ${role}`
                                : 'You do not hold platform:users:roles'
                            }
                            onClick={() =>
                              (held ? revoke : grant).mutate(
                                { id: u.id, role },
                                {
                                  onSuccess: () =>
                                    toast.success(`${held ? 'Revoked' : 'Granted'} ${role} for ${u.email}`),
                                  onError,
                                },
                              )
                            }
                            className={
                              held
                                ? 'rounded-full bg-accent px-2 py-0.5 text-xs text-accent-foreground disabled:opacity-60'
                                : 'rounded-full border border-dashed border-border px-2 py-0.5 text-xs text-muted-foreground hover:border-solid hover:text-foreground disabled:opacity-40'
                            }
                          >
                            {role}
                          </button>
                        );
                      })}
                    </div>
                  </TD>

                  <TD className="text-sm text-muted-foreground">
                    {u.forgejoUsername
                      ? <span className="font-mono text-xs">{u.forgejoUsername}</span>
                      : '—'}
                  </TD>

                  <TD className="text-sm text-muted-foreground">{date(u.createdAt)}</TD>

                  <TD>
                    <div className="flex items-center justify-end gap-1">
                      {mayWrite && !u.emailConfirmed && (
                        <Button
                          variant="ghost"
                          size="sm"
                          onClick={() =>
                            confirmEmail.mutate({ id: u.id }, {
                              onSuccess: () => toast.success(`Confirmed ${u.email}`),
                              onError,
                            })
                          }
                        >
                          Confirm email
                        </Button>
                      )}
                      {mayWrite && (
                        <Button
                          variant="outline"
                          size="sm"
                          // The server refuses this too — self-lock and last-SuperAdmin are
                          // enforced there, and audited when refused. Disabling it here is so
                          // the operator does not have to discover the rule from an error.
                          disabled={isSelf || setLock.isPending}
                          title={isSelf ? 'You cannot lock your own account' : undefined}
                          onClick={() =>
                            setLock.mutate({ id: u.id, locked: !u.lockedOut }, {
                              onSuccess: () =>
                                toast.success(`${u.lockedOut ? 'Unlocked' : 'Locked'} ${u.email}`),
                              onError,
                            })
                          }
                        >
                          {u.lockedOut ? 'Unlock' : 'Lock'}
                        </Button>
                      )}
                    </div>
                  </TD>
                </TR>
              );
            })}
          </TBody>
        </Table>
      )}
    </div>
  );
}
