import { Check, ChevronsUpDown, GitCommitHorizontal, Layers } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from '@tanstack/react-router';
import { Button, cn, Popover, PopoverContent, PopoverTrigger } from '@dcms/ui';
import { getCurrentTenantSlug, switchTenant, useMyTenants, workspaceOf } from '../tenants';

const isProduction = (name: string | null | undefined) => name === 'production';

/**
 * Which environment of the workspace this console is working in (ADR 0026), and one click to
 * another. Production is drawn in the destructive tone so nobody edits it thinking they are in
 * development. Absent while the workspace has no environments.
 */
export function EnvironmentSwitcher() {
  const { t } = useTranslation();
  const tenants = useMyTenants(true);
  const [open, setOpen] = useState(false);
  const navigate = useNavigate();
  const current = getCurrentTenantSlug();
  const active = tenants.data?.find((x) => x.slug === current);
  if (!active?.workspaceId) return null;

  const environments = (tenants.data ?? [])
    .filter((x) => workspaceOf(x) === active.workspaceId)
    .sort((a, b) => (a.environmentOrder ?? 0) - (b.environmentOrder ?? 0));
  const tone = (name: string | null | undefined) =>
    isProduction(name)
      ? 'border-destructive/40 bg-destructive/10 text-destructive hover:bg-destructive/15'
      : 'border-primary/30 bg-primary/10 text-primary hover:bg-primary/15';

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="outline"
          size="sm"
          className={cn('min-w-0 shrink gap-1.5', tone(active.environment))}
          aria-label={t('environments.switch', { name: active.environment })}
        >
          <Layers className="h-3.5 w-3.5 shrink-0" aria-hidden />
          <span className="truncate">{active.environment}</span>
          <ChevronsUpDown className="h-3.5 w-3.5 shrink-0 opacity-60" aria-hidden />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-56 p-1.5">
        <p className="px-2 py-1 text-xs font-medium text-muted-foreground">{t('environments.title')}</p>
        {environments.map((env) => (
          <button
            key={env.tenantId}
            type="button"
            onClick={() => {
              setOpen(false);
              if (env.slug !== current) switchTenant(env.slug, true);
            }}
            className="flex w-full items-center justify-between gap-2 rounded-sm px-2 py-1.5 text-sm hover:bg-accent"
          >
            <span className="flex min-w-0 items-center gap-2">
              <span
                aria-hidden
                className={cn('h-2 w-2 shrink-0 rounded-full', isProduction(env.environment) ? 'bg-destructive' : 'bg-primary')}
              />
              <span className="truncate">{env.environment}</span>
            </span>
            {env.slug === current ? <Check className="h-4 w-4 text-primary" /> : null}
          </button>
        ))}
        <div className="mt-1 border-t pt-1">
          <button
            type="button"
            onClick={() => {
              setOpen(false);
              const to: string = '/deployments';
              void navigate({ to });
            }}
            className="flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-sm hover:bg-accent"
          >
            <GitCommitHorizontal className="h-4 w-4" aria-hidden />
            {t('deployments.title')}
          </button>
        </div>
      </PopoverContent>
    </Popover>
  );
}
