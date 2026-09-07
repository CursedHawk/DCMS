import { Check, ChevronsUpDown, Plus } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useNavigate } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { Button, cn, Popover, PopoverContent, PopoverTrigger } from '@dcms/ui';
import {
  getCurrentTenantSlug,
  setCurrentTenantSlug,
  useMyTenants,
} from '../tenants';

export function TenantSwitcher() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const tenants = useMyTenants(true);
  const [open, setOpen] = useState(false);
  const [current, setCurrent] = useState(getCurrentTenantSlug() ?? '');

  // Auto-select the first tenant if none chosen yet.
  useEffect(() => {
    if (!current && tenants.data?.length) {
      const first = tenants.data[0].slug;
      setCurrent(first);
      setCurrentTenantSlug(first);
    }
  }, [tenants.data, current]);

  const active = tenants.data?.find((x) => x.slug === current);

  function pick(slug: string) {
    setCurrentTenantSlug(slug);
    setOpen(false);
    // Permissions + most data are tenant-scoped; reload to refetch cleanly.
    window.location.reload();
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className="min-w-44 justify-between">
          <span className="truncate">{active?.name ?? t('tenant.switcher')}</span>
          <ChevronsUpDown className="h-3.5 w-3.5 opacity-60" />
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
