import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { Badge, Button } from '@dcms/ui';
import { RefreshCw } from 'lucide-react';
import { api } from '../../../lib/api';
import { OutcomeView, type TraceEntry } from './RunView';

interface RunSummary {
  id: string;
  function: string;
  deployment: number;
  trigger: string;
  status: 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Terminated';
  attempts: number;
  error?: string | null;
  durationMs: number;
  createdAt: string;
}

interface RunDetail {
  run: RunSummary;
  input?: unknown;
  trigger?: unknown;
  result?: unknown;
  logs?: string | null;
  calls: number;
  trace?: TraceEntry[] | null;
}

/** What a replay hands the Run tab: the function, its input, and what to say started it. */
export interface Replay {
  fn: string;
  saved: { trigger: 'manual' | 'http' | 'row' | 'message' | 'schedule'; input: unknown };
  nonce: number;
}

const TONE = { Pending: 'secondary', Running: 'default', Succeeded: 'success', Failed: 'destructive', Terminated: 'warning' } as const;

function replayKind(trigger: string): Replay['saved']['trigger'] {
  if (trigger === 'row' || trigger === 'relation') return 'row';
  if (trigger === 'schedule' || trigger === 'every') return 'schedule';
  if (trigger === 'message' || trigger === 'http') return trigger;
  return 'manual';
}

/**
 * The Runs tab (workbench W4): the app's run history in this environment, newest first, and one
 * run in full: its input, what started it, its result or error (frames open the file), its host
 * calls and its log. "Replay against the draft" runs the same input through the Run tab, with
 * the code as it is now. Shown to holders of the app's logs permission; the server says so.
 */
export function RunsView({ functionsApi, initialFunction, onReplay }: {
  functionsApi: string;
  initialFunction?: string;
  onReplay: (replay: Replay) => void;
}) {
  const { t, i18n } = useTranslation();
  const [fn, setFn] = useState(initialFunction ?? '');
  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<string | null>(null);

  const query = new URLSearchParams({ page: String(page) });
  if (fn.trim()) query.set('function', fn.trim());
  if (status) query.set('status', status);
  const runs = useQuery({
    queryKey: ['functions-runs', functionsApi, fn.trim(), status, page],
    queryFn: () => api.get<{ items: RunSummary[]; more: boolean }>(`${functionsApi}/runs?${query}`),
  });
  const detail = useQuery({
    queryKey: ['functions-run', functionsApi, open],
    queryFn: () => api.get<RunDetail>(`${functionsApi}/runs/${open}`),
    enabled: !!open,
  });

  return (
    <div className="flex h-full min-h-0 text-sm" data-testid="runs-view">
      <div className="flex w-[26rem] shrink-0 flex-col border-r">
        <div className="flex gap-1 border-b p-2">
          <input
            className="min-w-0 flex-1 rounded border bg-background px-2 py-1 text-xs"
            placeholder={t('ide.runs.filterFunction')}
            value={fn}
            onChange={(e) => {
              setFn(e.target.value);
              setPage(1);
            }}
          />
          <Button size="icon" variant="ghost" aria-label={t('ide.runs.refresh')} onClick={() => void runs.refetch()} disabled={runs.isFetching}>
            <RefreshCw className={runs.isFetching ? 'h-4 w-4 animate-spin' : 'h-4 w-4'} />
          </Button>
          <select
            className="rounded border bg-background px-1 text-xs"
            value={status}
            aria-label={t('ide.runs.status')}
            onChange={(e) => {
              setStatus(e.target.value);
              setPage(1);
            }}
          >
            <option value="">{t('ide.runs.anyStatus')}</option>
            {Object.keys(TONE).map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </div>
        <ul className="min-h-0 flex-1 overflow-y-auto">
          {runs.isError && <li className="p-2 text-destructive">{(runs.error as Error).message}</li>}
          {runs.data?.items.length === 0 && <li className="p-2 text-muted-foreground">{t('ide.runs.empty')}</li>}
          {(runs.data?.items ?? []).map((r) => (
            <li key={r.id}>
              <button
                type="button"
                className={`flex w-full items-center gap-2 px-2 py-1 text-left hover:bg-muted ${open === r.id ? 'bg-muted' : ''}`}
                onClick={() => setOpen(r.id)}
              >
                <Badge tone={TONE[r.status]}>{r.status}</Badge>
                <span className="truncate font-mono">{r.function}</span>
                <span className="ml-auto shrink-0 text-xs text-muted-foreground">
                  {r.trigger} · {new Date(r.createdAt).toLocaleTimeString(i18n.language)}
                </span>
              </button>
            </li>
          ))}
        </ul>
        <div className="flex justify-between border-t p-1">
          <Button size="sm" variant="ghost" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
            {t('ide.runs.newer')}
          </Button>
          <Button size="sm" variant="ghost" disabled={!runs.data?.more} onClick={() => setPage((p) => p + 1)}>
            {t('ide.runs.older')}
          </Button>
        </div>
      </div>
      <div className="min-w-0 flex-1 overflow-y-auto p-2">
        {!open && <p className="text-muted-foreground">{t('ide.runs.pick')}</p>}
        {detail.data && (
          <div className="space-y-2">
            <div className="flex items-center gap-2">
              <span className="font-mono font-medium">{detail.data.run.function}</span>
              <span className="text-xs text-muted-foreground">
                {t('ide.runs.meta', { deployment: detail.data.run.deployment, trigger: detail.data.run.trigger, attempts: detail.data.run.attempts })}
              </span>
              <Button
                size="sm"
                variant="outline"
                className="ml-auto"
                onClick={() =>
                  onReplay({
                    fn: detail.data!.run.function,
                    saved: { trigger: replayKind(detail.data!.run.trigger), input: detail.data!.input ?? null },
                    nonce: Date.now(),
                  })
                }
              >
                {t('ide.runs.replay')}
              </Button>
            </div>
            <p className="text-xs font-medium">{t('ide.runs.input')}</p>
            <pre className="whitespace-pre-wrap rounded bg-muted p-2 font-mono text-xs">{JSON.stringify(detail.data.input ?? null, null, 2)}</pre>
            <OutcomeView
              outcome={{
                status: { Succeeded: 'ok', Failed: 'error', Terminated: 'limit' }[detail.data.run.status as string] ?? detail.data.run.status.toLowerCase(),
                result: detail.data.result,
                error: detail.data.run.error,
                logs: detail.data.logs ?? '',
                calls: detail.data.calls,
                durationMs: detail.data.run.durationMs,
                trace: detail.data.trace,
              }}
            />
          </div>
        )}
      </div>
    </div>
  );
}
