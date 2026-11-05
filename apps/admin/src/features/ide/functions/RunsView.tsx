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
 * The Runs sidebar view (workbench W4): the app's run history in this environment, newest first,
 * and under it the picked run in full: its input, what started it, its result or error (frames open the file), its host
 * calls and its log. "Replay against the draft" runs the same input through the Run tab under the editor, with
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
    <div className="flex h-full min-h-0 flex-col text-sm" data-testid="runs-view">
      <div className="flex h-9 shrink-0 items-center gap-1 border-b px-2">
        <span className="flex-1 truncate text-xs font-semibold uppercase tracking-wide text-muted-foreground">{t('ide.runs.title')}</span>
        <Button size="icon" variant="ghost" className="h-7 w-7" aria-label={t('ide.runs.refresh')} onClick={() => void runs.refetch()} disabled={runs.isFetching}>
          <RefreshCw className={runs.isFetching ? 'h-3.5 w-3.5 animate-spin' : 'h-3.5 w-3.5'} />
        </Button>
      </div>
      <div className="flex shrink-0 gap-1 border-b p-2">
        <input
          className="min-w-0 flex-1 rounded border bg-background px-2 py-1 text-xs"
          placeholder={t('ide.runs.filterFunction')}
          value={fn}
          onChange={(e) => {
            setFn(e.target.value);
            setPage(1);
          }}
        />
        <select
          className="w-24 rounded border bg-background px-1 text-xs"
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
      {/* The list on top, the picked run in full under it: a sidebar is tall and narrow. */}
      <ul className="max-h-[40%] min-h-24 shrink-0 overflow-y-auto border-b">
        {runs.isError && <li className="p-2 text-destructive">{(runs.error as Error).message}</li>}
        {runs.data?.items.length === 0 && <li className="p-2 text-muted-foreground">{t('ide.runs.empty')}</li>}
        {(runs.data?.items ?? []).map((r) => (
          <li key={r.id}>
            <button
              type="button"
              className={`flex w-full flex-col gap-0.5 px-2 py-1 text-left hover:bg-muted ${open === r.id ? 'bg-muted' : ''}`}
              onClick={() => setOpen(r.id)}
            >
              <span className="flex min-w-0 items-center gap-2">
                <Badge tone={TONE[r.status]}>{r.status}</Badge>
                <span className="truncate font-mono text-xs">{r.function}</span>
              </span>
              <span className="text-2xs text-muted-foreground">
                {r.trigger} · {new Date(r.createdAt).toLocaleString(i18n.language)} · {r.durationMs} ms
              </span>
            </button>
          </li>
        ))}
        {(page > 1 || runs.data?.more) && (
          <li className="flex justify-between p-1">
            <Button size="sm" variant="ghost" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
              {t('ide.runs.newer')}
            </Button>
            <Button size="sm" variant="ghost" disabled={!runs.data?.more} onClick={() => setPage((p) => p + 1)}>
              {t('ide.runs.older')}
            </Button>
          </li>
        )}
      </ul>
      <div className="min-h-0 flex-1 overflow-y-auto p-2" data-testid="run-detail">
        {!open && <p className="text-xs text-muted-foreground">{t('ide.runs.pick')}</p>}
        {detail.data && (
          <div className="space-y-2">
            <div className="font-mono text-xs font-medium">{detail.data.run.function}</div>
            <div className="text-2xs text-muted-foreground">
              {t('ide.runs.meta', { deployment: detail.data.run.deployment, trigger: detail.data.run.trigger, attempts: detail.data.run.attempts })}
            </div>
            <Button
              size="sm"
              variant="outline"
              className="w-full"
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
            <p className="text-xs font-medium">{t('ide.runs.input')}</p>
            <pre className="whitespace-pre-wrap break-all rounded bg-muted p-2 font-mono text-xs">{JSON.stringify(detail.data.input ?? null, null, 2)}</pre>
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
