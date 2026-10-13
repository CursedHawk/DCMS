import { Check, ChevronsUpDown, Plus } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useNavigate } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { Button, cn, Popover, PopoverContent, PopoverTrigger } from '@dcms/ui';
import { useMyPermissions } from '../lib/permissions';
import {
  getCurrentTenantSlug,
  setCurrentTenantSlug,
  useMyTenants,
} from '../tenants';

/**
 * A full load of the home page. Full, because permissions and almost every query are
 * tenant-scoped; home, because the current route usually names something of the old tenant
 * (a site, a plugin instance, a record) that the new one does not have.
 */
function goHome() {
  window.location.assign('/');
}

export function TenantSwitcher() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const tenants = useMyTenants(true);
  const me = useMyPermissions(true);
  const [open, setOpen] = useState(false);
  const [current, setCurrent] = useState(getCurrentTenantSlug() ?? '');

  // Select the first tenant when none is chosen, and replace a selection that is not this
  // account's. The slug lives in localStorage, so it outlives an account switch and a removal
  // from the workspace; every switch path ends in a full page load, which is why checking once
  // the list arrives is enough. SuperAdmins are exempt: they may hold any tenant, which is what
  // the platform console's ?tenant= links rely on.
  useEffect(() => {
    const list = tenants.data;
    if (!list || !me.data || list.some((x) => x.slug === current)) return;
    if (current && me.data.isSuperAdmin) return;
    const next = list[0]?.slug ?? null;
    setCurrentTenantSlug(next);
    // Also settles this effect: storing the slug changes the permissions query's key, and a
    // second run that still saw the stale slug would start a second navigation mid-load.
    setCurrent(next ?? '');
    // This page loaded under a tenant the account cannot use, and its route may not exist in
    // the next one.
    if (current) goHome();
  }, [tenants.data, me.data, current]);

  const active = tenants.data?.find((x) => x.slug === current);

  function pick(slug: string) {
    setCurrentTenantSlug(slug);
    setOpen(false);
    goHome();
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className="min-w-0 shrink justify-between sm:min-w-44">
          <span className="truncate">{active?.name ?? t('tenant.switcher')}</span>
          <ChevronsUpDown className="h-3.5 w-3.5 shrink-0 opacity-60" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-60 p-1.5">
        <p className="px-2 py-1 text-xs font-medium text-muted-foreground">{t('tenant.switcher')}</p>
        <div className="max-h-64 overflow-y-auto">
          {(tenants.data ?? []).map((tn) => (
            <button
              key={tn.tenantId}
              type="button"
              onClick={() => pick(tn.slug)}
              className="flex w-full items-center justify-between rounded-sm px-2 py-1.5 text-sm hover:bg-accent"
            >
              <span className="truncate">{tn.name}</span>
              {tn.slug === current ? <Check className="h-4 w-4 text-primary" /> : null}
            </button>
          ))}
          {tenants.data?.length === 0 ? (
            <p className="px-2 py-2 text-xs text-muted-foreground">{t('tenant.selectFirst')}</p>
          ) : null}
        </div>
        <div className="mt-1 border-t pt-1">
          <button
            type="button"
            onClick={() => {
              setOpen(false);
              const to: string = '/tenants';
              void navigate({ to });
            }}
            className={cn('flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-sm hover:bg-accent')}
          >
            <Plus className="h-4 w-4" />
            {t('tenant.createTenant')}
          </button>
        </div>
      </PopoverContent>
    </Popover>
  );
}
