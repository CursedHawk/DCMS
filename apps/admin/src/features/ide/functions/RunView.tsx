import { useMemo, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Badge, Button } from '@dcms/ui';
import { api } from '../../../lib/api';
import { useVfs } from '../../site-source';
import type { FunctionSpec } from './FunctionsView';

/** What a run answered: /_functions/test and /_functions/run. */
export interface RunOutcome {
  status: string;
  result?: unknown;
  error?: string | null;
  logs: string;
  calls: number;
  durationMs: number;
  trace?: TraceEntry[] | null;
}

export interface TraceEntry {
  op: string;
  args?: string | null;
  ok: boolean;
  error?: string | null;
  ms: number;
}

type TriggerKind = 'manual' | 'http' | 'row' | 'message' | 'schedule';
const KINDS: TriggerKind[] = ['manual', 'http', 'row', 'message', 'schedule'];

/** Where saved inputs live: repository files, so they are versioned and promoted with the code. */
export const INPUTS_DIR = 'test-inputs';

interface Saved {
  trigger: TriggerKind;
  input: unknown;
  user?: unknown;
}

/** A starting input for a trigger kind, shaped like what that trigger really hands the function. */
function template(kind: TriggerKind, fn?: FunctionSpec): Saved {
  const declared = fn?.triggers.find((t) => t.kind === kind);
  switch (kind) {
    case 'row': {
      const table = String(declared?.table ?? 'app.table');
      return {
        trigger: kind,
        input: { event: `${table.split('.')[1] ?? 'table'}.${declared?.event ?? 'updated'}`, table, app: table.split('.')[0], id: '00000000-0000-0000-0000-000000000000', changedFields: [] },
      };
    }
    case 'message':
      return { trigger: kind, input: {} };
    case 'http':
      return { trigger: kind, input: { hello: 'world' }, user: null };
    default:
      return { trigger: kind, input: null };
  }
}

/** The simulated context a test sends with the run (RunSimulation on the server). */
function simulation(saved: Saved, fn: string) {
  switch (saved.trigger) {
    case 'row': {
      const input = (saved.input ?? {}) as Record<string, unknown>;
      return { trigger: { kind: 'row', detail: { event: input.event ?? null, app: input.app ?? null, eventId: crypto.randomUUID() } } };
    }
    case 'message':
      return { trigger: { kind: 'message', detail: { topic: 'test' } } };
    case 'schedule':
      return { trigger: { kind: 'schedule', detail: { slot: new Date().toISOString() } } };
    case 'http':
      return {
        trigger: { kind: 'http', detail: { method: 'POST', path: `/fn/${fn}` } },
        request: { method: 'POST', path: `/fn/${fn}`, query: {}, headers: { 'content-type': 'application/json' }, body: saved.input ?? null },
        user: saved.user ?? null,
      };
    default:
      return undefined;
  }
}

/**
 * The Run tab (workbench W2): run one function now, built from the draft or as deployed, told it
 * was started by an HTTP call, a row event, a message or a schedule. It runs for real, under the
 * member's permissions (which must cover everything the code uses), so its writes happen. Inputs
 * can be kept as test-inputs/<function>/<name>.json in the repository.
 */
export function RunView({
  functionsApi,
  initial,
  onRan,
}: {
  functionsApi: string;
  /** A run to start from: the Runs tab's "replay" fills this in. */
  initial?: { fn: string; saved: Saved; nonce: number };
  onRan?: (outcome: RunOutcome) => void;
}) {
  const { t } = useTranslation();
  const files = useVfs((s) => s.files);
  const check = useQuery({
    queryKey: ['functions-check-run', functionsApi],
    queryFn: () => api.post<{ ok: boolean; specs: FunctionSpec[] }>(`${functionsApi}/check`, { files: useVfs.getState().files }),
    staleTime: 30_000,
  });
  const specs = check.data?.specs ?? [];

  const [fn, setFn] = useState('');
  const [target, setTarget] = useState<'draft' | 'live'>('draft');
  const [kind, setKind] = useState<TriggerKind>('manual');
  const [inputText, setInputText] = useState('null');
  const [userText, setUserText] = useState('null');
  const [saveName, setSaveName] = useState('');
  const [seen, setSeen] = useState<number>();
  const selected = fn || specs[0]?.name || '';
  const spec = specs.find((s) => s.name === selected);

  const load = (saved: Saved, name = selected) => {
    setFn(name);
    setKind(saved.trigger);
    setInputText(JSON.stringify(saved.input ?? null, null, 2));
    setUserText(JSON.stringify(saved.user ?? null, null, 2));
  };
  if (initial && initial.nonce !== seen) {
    setSeen(initial.nonce);
    load(initial.saved, initial.fn);
  }

  const savedInputs = useMemo(
    () => Object.keys(files).filter((p) => p.startsWith(`${INPUTS_DIR}/${selected}/`) && p.endsWith('.json')).sort(),
    [files, selected],
  );

  const parse = (text: string, what: string): unknown => {
    try {
      return JSON.parse(text || 'null');
    } catch {
      throw new Error(t('ide.run.badJson', { what }));
    }
  };

  const run = useMutation({
    mutationFn: async () => {
      const saved: Saved = { trigger: kind, input: parse(inputText, t('ide.run.input')), user: parse(userText, t('ide.run.user')) };
      const simulate = simulation(saved, selected);
      return target === 'draft'
        ? api.post<RunOutcome>(`${functionsApi}/test`, { files: useVfs.getState().files, function: selected, input: saved.input, simulate })
        : api.post<RunOutcome>(`${functionsApi}/run`, { function: selected, input: saved.input, simulate });
    },
    onSuccess: (outcome) => onRan?.(outcome),
    onError: (e: Error) => toast.error(e.message),
  });

  const save = () => {
    const name = saveName.trim().replace(/[^A-Za-z0-9_-]/g, '-');
    if (!name || !selected) return;
    const path = `${INPUTS_DIR}/${selected}/${name}.json`;
    const body = JSON.stringify({ trigger: kind, input: parse(inputText, t('ide.run.input')), user: parse(userText, t('ide.run.user')) }, null, 2) + '\n';
    const vfs = useVfs.getState();
    if (vfs.files[path] == null) vfs.createFile(path, body);
    else vfs.writeFile(path, body);
    toast.success(t('ide.run.saved', { path }));
  };

  const outcome = run.data;
  return (
    <div className="flex h-full min-h-0 text-sm" data-testid="run-view">
      <div className="flex w-[22rem] shrink-0 flex-col gap-2 overflow-y-auto border-r p-2">
        <label className="flex flex-col gap-1">
          <span className="text-xs text-muted-foreground">{t('ide.run.function')}</span>
          <select className="rounded border bg-background px-2 py-1" value={selected} onChange={(e) => setFn(e.target.value)} aria-label={t('ide.run.function')}>
            {specs.map((s) => (
              <option key={s.name} value={s.name}>
                {s.name} ({s.runtime === 'dotnet' ? 'C#' : 'TS'})
              </option>
            ))}
          </select>
        </label>
        <div className="flex gap-2">
          <label className="flex flex-1 flex-col gap-1">
            <span className="text-xs text-muted-foreground">{t('ide.run.target')}</span>
            <select className="rounded border bg-background px-2 py-1" value={target} onChange={(e) => setTarget(e.target.value as 'draft' | 'live')}>
              <option value="draft">{t('ide.run.draft')}</option>
              <option value="live">{t('ide.run.live')}</option>
            </select>
          </label>
          <label className="flex flex-1 flex-col gap-1">
            <span className="text-xs text-muted-foreground">{t('ide.run.trigger')}</span>
            <select
              className="rounded border bg-background px-2 py-1"
              value={kind}
              aria-label={t('ide.run.trigger')}
              onChange={(e) => load(template(e.target.value as TriggerKind, spec))}
            >
              {KINDS.map((k) => (
                <option key={k} value={k}>
                  {t(`ide.run.kinds.${k}`)}
                </option>
              ))}
            </select>
          </label>
        </div>
        <label className="flex flex-col gap-1">
          <span className="text-xs text-muted-foreground">{t('ide.run.input')}</span>
          <textarea
            className="h-28 rounded border bg-background p-1 font-mono text-xs"
            value={inputText}
            onChange={(e) => setInputText(e.target.value)}
            aria-label={t('ide.run.input')}
            spellCheck={false}
          />
        </label>
        {kind === 'http' && (
          <label className="flex flex-col gap-1">
            <span className="text-xs text-muted-foreground">{t('ide.run.user')}</span>
            <textarea
              className="h-16 rounded border bg-background p-1 font-mono text-xs"
              value={userText}
              onChange={(e) => setUserText(e.target.value)}
              spellCheck={false}
            />
          </label>
        )}
        <Button size="sm" onClick={() => run.mutate()} disabled={!selected || run.isPending}>
          {run.isPending ? t('ide.run.running') : t('ide.run.run')}
        </Button>
        <p className="text-xs text-muted-foreground">{t('ide.run.forReal')}</p>
        <div className="flex gap-1">
          <input
            className="min-w-0 flex-1 rounded border bg-background px-2 py-1 text-xs"
            placeholder={t('ide.run.saveAs')}
            value={saveName}
            onChange={(e) => setSaveName(e.target.value)}
          />
          <Button size="sm" variant="outline" onClick={save} disabled={!saveName.trim()}>
            {t('ide.run.save')}
          </Button>
        </div>
        {savedInputs.length > 0 && (
          <ul className="space-y-0.5 text-xs">
            {savedInputs.map((path) => (
              <li key={path}>
                <button
                  type="button"
                  className="font-mono hover:underline"
                  onClick={() => {
                    try {
                      load(JSON.parse(useVfs.getState().files[path] ?? 'null') as Saved);
                    } catch {
                      toast.error(t('ide.run.badJson', { what: path }));
                    }
                  }}
                >
                  {path.split('/').pop()}
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
      <div className="min-w-0 flex-1 overflow-y-auto p-2">
        {outcome ? <OutcomeView outcome={outcome} /> : <p className="text-muted-foreground">{t('ide.run.nothingYet')}</p>}
      </div>
    </div>
  );
}

/** A run's answer: status, result or error (its stack frames open the file), logs, and its host calls. */
export function OutcomeView({ outcome }: { outcome: RunOutcome }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-2" data-testid="run-outcome">
      <div className="flex items-center gap-2">
        <Badge tone={outcome.status === 'ok' ? 'success' : outcome.status === 'limit' ? 'warning' : 'destructive'}>{outcome.status}</Badge>
        <span className="text-xs text-muted-foreground">{t('ide.run.stats', { ms: outcome.durationMs, calls: outcome.calls })}</span>
      </div>
      {outcome.error ? <ErrorText text={outcome.error} /> : <pre className="whitespace-pre-wrap rounded bg-muted p-2 font-mono text-xs">{JSON.stringify(outcome.result ?? null, null, 2)}</pre>}
      {outcome.trace && outcome.trace.length > 0 && (
        <div>
          <p className="text-xs font-medium">{t('ide.run.trace')}</p>
          <ol className="mt-1 space-y-0.5 font-mono text-xs" data-testid="run-trace">
            {outcome.trace.map((entry, i) => (
              <li key={i} className={entry.ok ? '' : 'text-destructive'}>
                <span className="text-muted-foreground">{entry.ms} ms</span> {entry.op} {entry.args}
                {entry.error ? ` — ${entry.error}` : ''}
              </li>
            ))}
          </ol>
        </div>
      )}
      {outcome.logs && (
        <div>
          <p className="text-xs font-medium">{t('ide.run.logs')}</p>
          <pre className="mt-1 whitespace-pre-wrap rounded bg-muted p-2 font-mono text-xs">{outcome.logs}</pre>
        </div>
      )}
    </div>
  );
}

/** "in functions/X.cs:line 12" (C#) and "functions/x.ts:12:5" (TypeScript, W3) open the file at that line. */
const FRAME = /((?:[A-Za-z0-9_.-]+\/)*[A-Za-z0-9_.-]+\.(?:cs|ts|js))(?::line |:)(\d+)/g;

export function ErrorText({ text }: { text: string }) {
  const parts: React.ReactNode[] = [];
  let last = 0;
  for (const match of text.matchAll(FRAME)) {
    const [whole, path, line] = match;
    parts.push(text.slice(last, match.index));
    parts.push(
      <button
        key={match.index}
        type="button"
        className="underline"
        onClick={() => useVfs.getState().revealAt(path, Number(line))}
      >
        {whole}
      </button>,
    );
    last = match.index! + whole.length;
  }
  parts.push(text.slice(last));
  return <pre className="whitespace-pre-wrap rounded bg-destructive/10 p-2 font-mono text-xs text-destructive">{parts}</pre>;
}
