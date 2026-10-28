import { Check, ChevronsUpDown, GitCommitHorizontal, Layers } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from '@tanstack/react-router';
import { Button, cn, Popover, PopoverContent, PopoverTrigger } from '@dcms/ui';
import { useEnvironments } from '../features/environments/api';
import { useMyPermissions } from '../lib/permissions';
import { getCurrentEnvironment, switchEnvironment } from '../tenants';

const isProduction = (name: string | null | undefined) => name === 'production';

/**
 * Which environment of the workspace this console is working in (ADR 0028), and one click to
 * another. Production is drawn in the destructive tone so nobody edits it thinking they are in
 * development. Absent while the workspace has production alone.
 */
export function EnvironmentSwitcher() {
  const { t } = useTranslation();
  const environments = useEnvironments();
  const me = useMyPermissions(true);
  const [open, setOpen] = useState(false);
  const navigate = useNavigate();
  const current = getCurrentEnvironment() ?? 'production';
  const list = environments.data?.environments ?? [];
  const active = list.find((e) => e.name === current);

  // The selection lives in localStorage and outlives the environment, and the caller's role in it:
  // move to one they can open -- production first -- with a full load, as every switch does.
  useEffect(() => {
    const all = environments.data?.environments;
    if (!all || !me.data || me.data.isSuperAdmin) return;
    if (all.find((e) => e.name === current)?.isMember) return;
    const next = all.find((e) => e.isProduction && e.isMember) ?? all.find((e) => e.isMember);
    if (next && next.name !== current) switchEnvironment(next.name);
  }, [environments.data, me.data, current]);

  if (!environments.data?.enabled || !active) return null;
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
          className={cn('min-w-0 shrink gap-1.5', tone(active.name))}
          aria-label={t('environments.switch', { name: active.name })}
        >
          <Layers className="h-3.5 w-3.5 shrink-0" aria-hidden />
          <span className="truncate">{active.name}</span>
          <ChevronsUpDown className="h-3.5 w-3.5 shrink-0 opacity-60" aria-hidden />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-56 p-1.5">
        <p className="px-2 py-1 text-xs font-medium text-muted-foreground">{t('environments.title')}</p>
        {list.filter((env) => env.isMember || me.data?.isSuperAdmin).map((env) => (
          <button
            key={env.id}
            type="button"
            onClick={() => {
              setOpen(false);
              if (env.name !== current) switchEnvironment(env.name);
            }}
            className="flex w-full items-center justify-between gap-2 rounded-sm px-2 py-1.5 text-sm hover:bg-accent"
          >
            <span className="flex min-w-0 items-center gap-2">
              <span
                aria-hidden
                className={cn('h-2 w-2 shrink-0 rounded-full', env.isProduction ? 'bg-destructive' : 'bg-primary')}
              />
              <span className="truncate">{env.name}</span>
            </span>
            {env.name === current ? <Check className="h-4 w-4 text-primary" /> : null}
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
