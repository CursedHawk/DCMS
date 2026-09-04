import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Button, CenteredSpinner, toastApiError } from '@dcms/admin-ui';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { platformApi } from '../../lib/api';

interface RolePermissions { roleName: string; permissions: string[] }
interface Catalog { permissions: string[]; readOnly: string[] }

/**
 * Which platform role can reach which area.
 *
 * <p>This page is the reason the permission model exists. The console ships SuperAdmin-only,
 * but every screen is gated on a key rather than on the role — so opening an area to the
 * support team is a checkbox here, not a deploy. SuperAdmin is shown and not editable: the
 * authorization handler short-circuits on that role, so unchecking a box would change the
 * stored rows and nothing about what a SuperAdmin can do, which is worse than not offering it.</p>
 */
export function AccessPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const roles = useQuery({ queryKey: ['platform-roles'], queryFn: () => platformApi.get<RolePermissions[]>('/roles') });
  const catalog = useQuery({ queryKey: ['platform-permission-catalog'], queryFn: () => platformApi.get<Catalog>('/permissions/catalog') });

  const save = useMutation({
    mutationFn: ({ role, permissions }: { role: string; permissions: string[] }) =>
      platformApi.put(`/roles/${encodeURIComponent(role)}/permissions`, { permissions }),
    onSuccess: (_d, v) => {
      toast.success(`Saved ${v.role}`);
      void qc.invalidateQueries({ queryKey: ['platform-roles'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const [draft, setDraft] = useState<Record<string, Set<string>> | null>(null);

  if (roles.isLoading || catalog.isLoading) return <CenteredSpinner />;
  if (!roles.data || !catalog.data) return null;

  const editable = roles.data.filter((r) => r.roleName !== 'SuperAdmin');
  const superAdmin = roles.data.find((r) => r.roleName === 'SuperAdmin');

  const held = (role: string, key: string) =>
    draft?.[role]?.has(key) ?? roles.data!.find((r) => r.roleName === role)?.permissions.includes(key) ?? false;

  const toggle = (role: string, key: string) => {
    setDraft((prev) => {
      const next = { ...(prev ?? {}) };
      const current = new Set(next[role] ?? roles.data!.find((r) => r.roleName === role)?.permissions ?? []);
      if (current.has(key)) current.delete(key); else current.add(key);
      next[role] = current;
      return next;
    });
  };

  return (
    <div className="mx-auto w-full max-w-3xl px-6 py-8">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold tracking-tight">Access</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Which platform role reaches which part of this console. Roles themselves are granted
          to people on the Users page.
        </p>
      </header>

      {superAdmin && (
        <section className="mb-8 rounded-md border border-border bg-card p-4">
          <h2 className="text-sm font-medium">SuperAdmin</h2>
          <p className="mt-1 text-sm text-muted-foreground">
            Holds everything, always. The permission check short-circuits on this role, so these
            {' '}{superAdmin.permissions.length} keys are recorded for completeness and cannot be
            edited — removing one would change the row and not the access.
          </p>
        </section>
      )}

      {editable.map((role) => {
        const dirty = draft?.[role.roleName] !== undefined;
        return (
          <section key={role.roleName} className="mb-8 border-t border-border pt-5">
            <div className="mb-3 flex items-center justify-between gap-3">
              <h2 className="text-sm font-medium">{role.roleName}</h2>
              {dirty && (
                <div className="flex gap-2">
                  <Button variant="ghost" size="sm" onClick={() => setDraft(null)}>Discard</Button>
                  <Button
                    size="sm"
                    disabled={save.isPending}
                    onClick={() =>
                      save.mutate(
                        { role: role.roleName, permissions: [...(draft![role.roleName] ?? [])] },
                        { onSuccess: () => setDraft(null) },
                      )
                    }
                  >
                    Save changes
                  </Button>
                </div>
              )}
            </div>

            <ul className="grid gap-1 sm:grid-cols-2">
              {catalog.data!.permissions.map((key) => (
                <li key={key}>
                  <label className="flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-secondary">
                    <input
                      type="checkbox"
                      checked={held(role.roleName, key)}
                      onChange={() => toggle(role.roleName, key)}
                      className="h-3.5 w-3.5 accent-[hsl(var(--primary))]"
                    />
                    <span className="font-mono text-xs">{key}</span>
                  </label>
                </li>
              ))}
            </ul>
          </section>
        );
      })}
    </div>
  );
}
