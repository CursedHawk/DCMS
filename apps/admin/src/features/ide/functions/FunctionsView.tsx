import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Badge, Button, Dialog, DialogContent, DialogHeader, DialogTitle } from '@dcms/ui';
import { BookOpen, Plus, RefreshCw } from 'lucide-react';
import { AgentMarkdown } from '../../../components/AgentMarkdown';
import { api } from '../../../lib/api';
import { useVfs } from '../../site-source';
import { typeText, type Schema } from './typeText';

/** A function as the build read it (DeploymentService.DraftCheck.Specs). */
export interface FunctionSpec {
  name: string;
  runtime: string;
  description?: string | null;
  triggers: Record<string, unknown>[];
  http: string[];
  uses: { tables: Record<string, string[]>; contracts: string[]; egress: string[]; secrets: string[]; emits: string[] };
  file?: string | null;
  /** The methods it answers over HTTP (the server fills in GET and POST when it declared none). */
  httpMethods?: string[];
  /** What it takes and returns, as JSON Schema read from its types; absent or {} is any JSON. */
  input?: Schema | null;
  output?: Schema | null;
}

const short = (text: string) => (text.length > 160 ? `${text.slice(0, 159)}…` : text);

interface DraftCheck {
  ok: boolean;
  problems: { message: string; file?: string | null; line?: number | null }[];
  specs: FunctionSpec[];
  permissions: string[];
  /** What each function ships, in bytes: its bundle, or for C# the build's assemblies. */
  sizes?: Record<string, number>;
}

const kb = (bytes: number) => (bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} kB`);

interface Deployment {
  id: string;
  number: number;
  commitSha: string;
  status: string;
  functions: string[];
  activatedAt?: string | null;
}

/** "When crm.deals is updated", "Every 15m": one trigger, read aloud. */
export function describeTrigger(trigger: Record<string, unknown>, t: (key: string, o?: Record<string, unknown>) => string): string {
  const s = (key: string) => String(trigger[key] ?? '');
  switch (s('kind')) {
    case 'row':
    case 'relation':
      return t('ide.fn.trigger.row', { table: s('table') || s('relationship'), event: s('event') });
    case 'app':
      return t('ide.fn.trigger.app', { app: s('app') });
    case 'message':
      return t('ide.fn.trigger.message', { topic: s('topic') });
    case 'schedule':
      return t('ide.fn.trigger.schedule', { cron: s('cron') });
    case 'every':
      return t('ide.fn.trigger.every', { interval: s('interval') });
    case 'event':
      return t('ide.fn.trigger.event', { name: s('name') });
    default:
      return s('kind');
  }
}

/**
 * The Functions view of a Functions app's IDE (ADR 0029, 0030; workbench W1): every function the
 * draft defines, as the server's build reads it, and what is live. The draft is checked when the
 * view opens and again a moment after the files stop changing; the build is the server's, so
 * what it lists is exactly what a deployment would hold.
 */
export function FunctionsView({ functionsApi, onNew }: { functionsApi: string; onNew?: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const rev = useVfs((s) => s.rev);
  const [settledRev, setSettledRev] = useState(rev);
  useEffect(() => {
    const timer = setTimeout(() => setSettledRev(rev), 1500);
    return () => clearTimeout(timer);
  }, [rev]);

  const check = useQuery({
    queryKey: ['functions-check', functionsApi, settledRev],
    queryFn: () => api.post<DraftCheck>(`${functionsApi}/check`, { files: useVfs.getState().files }),
    placeholderData: (previous) => previous,
    staleTime: Infinity,
  });
  const deployments = useQuery({
    queryKey: ['functions-deployments', functionsApi],
    queryFn: () => api.get<Deployment[]>(`${functionsApi}/deployments`),
  });
  const activate = useMutation({
    mutationFn: (id: string) => api.post(`${functionsApi}/deployments/${id}/activate`, {}),
    onSuccess: () => {
      toast.success(t('ide.fn.deployed'));
      void queryClient.invalidateQueries({ queryKey: ['functions-deployments', functionsApi] });
    },
    onError: (e: Error) => toast.error(e.message),
  });

  // What a function can use: the platform's own page, read when asked for.
  const [help, setHelp] = useState(false);
  const runtime = useQuery({
    queryKey: ['functions-runtime', functionsApi],
    queryFn: () => api.get<{ markdown: string }>(`${functionsApi}/runtime`),
    enabled: help,
    staleTime: Infinity,
  });

  const live = deployments.data?.find((d) => d.status === 'Active');
  const ready = deployments.data?.find((d) => d.status === 'Ready' && (!live || d.number > live.number));
  const open = (file?: string | null) => file && useVfs.getState().open(file);

  return (
    <div className="flex h-full flex-col overflow-hidden text-sm" data-testid="functions-view">
      <Dialog open={help} onOpenChange={setHelp}>
        <DialogContent wide className="max-h-[85vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle>{t('ide.fn.runtime')}</DialogTitle>
          </DialogHeader>
          {runtime.data ? <AgentMarkdown text={runtime.data.markdown.replace(/^# .*\n/, '')} className="text-sm" /> : null}
          {runtime.isError && <p className="text-destructive">{(runtime.error as Error).message}</p>}
        </DialogContent>
      </Dialog>
      <div className="flex items-center gap-1 border-b px-3 py-2">
        <span className="font-medium">{t('ide.fn.title')}</span>
        <span className="flex-1" />
        <Button size="icon" variant="ghost" aria-label={t('ide.fn.runtime')} title={t('ide.fn.runtime')} onClick={() => setHelp(true)}>
          <BookOpen className="h-4 w-4" />
        </Button>
        {onNew && (
          <Button size="icon" variant="ghost" aria-label={t('ide.newFn.title')} title={t('ide.newFn.title')} onClick={onNew}>
            <Plus className="h-4 w-4" />
          </Button>
        )}
        <Button
          size="icon"
          variant="ghost"
          aria-label={t('ide.fn.recheck')}
          onClick={() => void check.refetch()}
          disabled={check.isFetching}
        >
          <RefreshCw className={check.isFetching ? 'h-4 w-4 animate-spin' : 'h-4 w-4'} />
        </Button>
      </div>

      <div className="flex-1 space-y-3 overflow-y-auto p-3">
        {check.data && !check.data.ok && (
          <p className="text-destructive">{t('ide.fn.problems', { count: check.data.problems.length })}</p>
        )}
        {check.isError && <p className="text-destructive">{(check.error as Error).message}</p>}
        {check.data?.ok && check.data.specs.length === 0 && <p className="text-muted-foreground">{t('ide.fn.none')}</p>}

        <ul className="space-y-2">
          {(check.data?.specs ?? []).map((fn) => (
            <li key={fn.name} className="rounded border p-2" data-testid={`function-${fn.name}`}>
              <div className="flex items-center gap-2">
                <button type="button" className="truncate font-mono font-medium hover:underline" onClick={() => open(fn.file)}>
                  {fn.name}
                </button>
                <Badge tone="secondary">{fn.runtime === 'dotnet' ? 'C#' : 'TS'}</Badge>
                {live?.functions.includes(fn.name) && <Badge tone="success">{t('ide.fn.live')}</Badge>}
                <span className="flex-1" />
                {check.data?.sizes?.[fn.name] !== undefined && (
                  <span className="shrink-0 text-xs text-muted-foreground" title={t('ide.fn.size')}>{kb(check.data.sizes[fn.name])}</span>
                )}
              </div>
              {fn.description && <p className="mt-1 text-muted-foreground">{fn.description}</p>}
              <ul className="mt-1 space-y-0.5 text-xs text-muted-foreground">
                {fn.triggers.map((trigger, i) => (
                  <li key={i}>{describeTrigger(trigger, t)}</li>
                ))}
                {fn.http.length > 0 && (
                  <li>
                    {t('ide.fn.http', { who: fn.http.join(', ') })}
                    {fn.httpMethods?.length ? <span className="font-mono"> · {fn.httpMethods.map((m) => m.toUpperCase()).join(', ')}</span> : null}
                  </li>
                )}
                {fn.http.length > 0 && (
                  <li className="font-mono" title={typeText(fn.input)}>
                    {t('ide.fn.takes')} {short(typeText(fn.input))}
                  </li>
                )}
                {fn.http.length > 0 && (
                  <li className="font-mono" title={typeText(fn.output)}>
                    {t('ide.fn.returns')} {short(typeText(fn.output))}
                  </li>
                )}
                {Object.entries(fn.uses.tables).map(([table, verbs]) => (
                  <li key={table}>{t('ide.fn.table', { table, verbs: verbs.join(', ') })}</li>
                ))}
                {fn.uses.contracts.length > 0 && <li>{t('ide.fn.contracts', { list: fn.uses.contracts.join(', ') })}</li>}
                {fn.uses.egress.length > 0 && <li>{t('ide.fn.egress', { list: fn.uses.egress.join(', ') })}</li>}
                {fn.uses.secrets.length > 0 && <li>{t('ide.fn.secrets', { list: fn.uses.secrets.join(', ') })}</li>}
              </ul>
            </li>
          ))}
        </ul>

        <div className="border-t pt-3">
          <p className="font-medium">{t('ide.fn.deployment')}</p>
          {live ? (
            <p className="text-muted-foreground">
              {t('ide.fn.liveDeployment', { number: live.number, commit: live.commitSha.slice(0, 7) })}
            </p>
          ) : (
            <p className="text-muted-foreground">{t('ide.fn.nothingLive')}</p>
          )}
          {ready && (
            <Button size="sm" className="mt-2" onClick={() => activate.mutate(ready.id)} disabled={activate.isPending}>
              {t('ide.fn.deploy', { number: ready.number })}
            </Button>
          )}
          <p className="mt-2 text-xs text-muted-foreground">{t('ide.fn.howToShip')}</p>
        </div>
      </div>
    </div>
  );
}
