import { useMemo, useState } from 'react';
import {
  Badge,
  Button,
  DataTable,
  EmptyState,
  FilterBar,
  PermissionTooltip,
  toastApiError,
  type Column,
} from '@dcms/ui';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { count, date } from '../../lib/format';
import { can, useMe, Perm } from '../../lib/permissions';
import {
  useConfirmEmail, useGrantRole, useRevokeRole, useSetLock, useUsers, type UserRow,
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
  const rolesBusy = grant.isPending || revoke.isPending;

  const columns: Column<UserRow>[] = useMemo(
    () => [
      {
        id: 'account',
        header: 'Account',
        primary: true,
        sortValue: (u) => u.email ?? u.displayName ?? null,
        cell: (u) => (
          <div className="flex flex-col">
            <span>{u.email ?? '—'}</span>
            <span className="text-xs text-muted-foreground">
              {u.displayName ?? 'No display name'}
              {u.lockedOut && ' · Locked out'}
              {!u.emailConfirmed && ' · Email unconfirmed'}
            </span>
          </div>
        ),
      },
      {
        id: 'roles',
        header: 'Platform roles',
        cell: (u) => (
          <div className="flex flex-wrap gap-1">
            {GLOBAL_ROLES.map((role) => {
              const held = u.roles.includes(role);
              return (
                <PermissionTooltip
                  key={role}
                  perm={Perm.UsersRoles}
                  reason="Changing platform roles needs platform:users:roles"
                >
                  <button
                    type="button"
                    disabled={!mayChangeRoles || rolesBusy}
                    aria-pressed={held}
                    aria-label={`${held ? 'Revoke' : 'Grant'} ${role} for ${u.email ?? u.id}`}
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
                </PermissionTooltip>
              );
            })}
          </div>
        ),
      },
      {
        id: 'git',
        header: 'Git',
        srHeader: 'Git account',
        cell: (u) =>
          u.forgejoUsername ? (
            <span className="font-mono text-xs">{u.forgejoUsername}</span>
          ) : (
            <span className="text-muted-foreground">—</span>
          ),
      },
      {
        id: 'joined',
        header: 'Joined',
        sortValue: (u) => u.createdAt ?? null,
        cell: (u) => <span className="text-muted-foreground">{date(u.createdAt)}</span>,
        align: 'right',
      },
      {
        id: 'actions',
        header: '',
        srHeader: 'Actions',
        align: 'right',
        cell: (u) => {
          const isSelf = u.id === me.data?.userId;
          return (
            <div className="flex items-center justify-end gap-1">
              {mayWrite && !u.emailConfirmed && (
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() =>
                    confirmEmail.mutate(
                      { id: u.id },
                      { onSuccess: () => toast.success(`Confirmed ${u.email}`), onError },
                    )
                  }
                >
                  Confirm email
                </Button>
              )}
              {mayWrite && (
                <Button
                  variant="outline"
                  size="sm"
                  // The server refuses this too — self-lock and last-SuperAdmin are enforced
                  // there, and audited when refused. Disabling it here is so the operator does
                  // not have to discover the rule from an error.
                  disabled={isSelf || setLock.isPending}
                  title={isSelf ? 'You cannot lock your own account' : undefined}
                  onClick={() =>
                    setLock.mutate(
                      { id: u.id, locked: !u.lockedOut },
                      {
                        onSuccess: () =>
                          toast.success(`${u.lockedOut ? 'Unlocked' : 'Locked'} ${u.email}`),
                        onError,
                      },
                    )
                  }
                >
                  {u.lockedOut ? 'Unlock' : 'Lock'}
                </Button>
              )}
            </div>
          );
        },
      },
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [mayWrite, mayChangeRoles, rolesBusy, setLock.isPending, me.data?.userId],
  );

  return (
    <div className="mx-auto w-full max-w-5xl px-4 py-6 sm:px-6 sm:py-8">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold tracking-tight">Users</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Every account on the platform. One identity spans all workspaces; roles here are
          platform-wide, not per workspace.
        </p>
      </header>

      <FilterBar
        search={search}
        onSearchChange={setSearch}
        searchPlaceholder="Search by email or name"
        activeCount={lockedOnly ? 1 : 0}
        onClear={() => setLockedOnly(false)}
        actions={
          users.data ? (
            <Badge tone="outline">
              {count(users.data.total)} account{users.data.total === 1 ? '' : 's'}
            </Badge>
          ) : null
        }
      >
        <Button
          variant={lockedOnly ? 'subtle' : 'outline'}
          size="sm"
          aria-pressed={lockedOnly}
          onClick={() => setLockedOnly((v) => !v)}
        >
          Locked out only
        </Button>
      </FilterBar>

      <DataTable
        rows={users.data?.items}
        columns={columns}
        rowKey={(u) => u.id}
        isLoading={users.isLoading}
        caption="Platform user accounts"
        defaultSort={{ columnId: 'account' }}
        empty={
          <EmptyState
            title={lockedOnly ? 'Nobody is locked out' : 'No account matches that'}
            description={lockedOnly ? undefined : 'Try part of the email address.'}
          />
        }
      />
    </div>
  );
}
