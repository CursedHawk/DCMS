import { useCallback, useRef, useState } from 'react';
import type { Classification } from '../../agent/classify';
import type { AgentEvent, ApprovalDecision, ToolCall, ToolSpec } from '../../agent/contracts';
import { DEFAULT_MODE, type AiMode } from '../../agent/modes';
import { runAgent } from '../../agent/runtime';
import { createTransaction, type FileChange } from '../../agent/transaction';
import { currentIndex, currentWorkspace, vfsPort } from '../../agent/vfsPort';
import { previewAvailable, requestBuild } from '../preview/buildBroker';
import { baselineTypeProblems, checkTypes } from '../diagnostics/checkTypes';
import { combineVerdict, newProblems } from '../diagnostics/typeCheck';
// The store itself, not the feature barrel: that barrel also exports the Monaco editor, and
// pulling it in here would drag the whole editor chunk into every module that touches the
// agent — including its tests, which have no DOM for it.
import { useVfs } from '../../site-source/vfs';
import { type Message, NoApiKeyError, streamAssistantTurn } from './client';
import {
  type AgentRunMetrics,
  finishRun,
  recordToolCall,
  recordTurn,
  startRun,
} from './runMetrics';
import { usePermissions } from '@dcms/ui';
import {
  appendMessages,
  conversationsKey,
  recordRun,
  titleFrom,
} from '../../assistant/conversations';
import { api } from '../../../lib/api';
import { useQueryClient } from '@tanstack/react-query';
import { ALL_TOOLS, formatProblems } from './checkTools';
import { DEFAULT_SCOPE, toolsFor, rememberScope, type AgentScope } from '../../agent/scope';
import { SANDBOX_TOOLS, SKILL_TOOLS, TENANT_TOOLS, type TenantToolContext } from './tenantTools';
import { buildSystemPrompt } from './systemPrompt';

/**
 * The Mode B IDE's agent session.
 *
 * <p>The loop itself now lives in `features/agent/runtime.ts`, shared with the console
 * assistant; what remains here is this surface's wiring — which tools, which prompt, and how the
 * event stream becomes React state.</p>
 */

const MAX_TOKENS = 16000;

/**
 * One thing that happened in a run, as the panel shows it.
 *
 * <p>This used to be `{ type, text, isError }` for everything, which meant a tool call was a
 * grey line reading `edit_file` and nothing else — no arguments, no result, no duration. The
 * whole point of watching an agent work is being able to see what it actually did, so the
 * panel needs the structure rather than a rendering of it.</p>
 */
export type AgentEntry =
  | { id: string; kind: 'user'; text: string }
  | { id: string; kind: 'assistant'; text: string }
  /** Streamed reasoning. Collapsed by default: it is context, not the answer. */
  | { id: string; kind: 'thinking'; text: string }
  | {
      id: string;
      kind: 'tool';
      name: string;
      /** What it was asked to do, in the model's own arguments. */
      input: Record<string, unknown>;
      label: string;
      status: 'running' | 'ok' | 'error' | 'declined';
      /** The tool's output once it has one. Capped by the runtime before it reaches here. */
      result?: string;
      /** Paths this call changed, so a card can link to the file it wrote. */
      paths?: string[];
      startedAt: number;
      endedAt?: number;
    }
  | { id: string; kind: 'check'; status: 'running' | 'passed' | 'failed' | 'skipped' }
  | { id: string; kind: 'retry'; attempt: number; of: number; because: string }
  | { id: string; kind: 'error'; text: string };

export interface PendingApproval {
  tools: readonly ToolCall[];
}

export interface AgentSession {
  entries: AgentEntry[];
  running: boolean;
  mode: AiMode;
  setMode: (m: AiMode) => void;
  /** How far outside the site's files this run may reach. Separate axis from mode. */
  scope: AgentScope;
  setScope: (s: AgentScope) => void;
  needsKey: boolean;
  keyProvider: string | null;
  pending: PendingApproval | null;
  /** How the last run was classified, so the panel can say why it did or did not plan. */
  classification: Classification | null;
  /** Files the last run touched, for the change-review pane. */
  changes: FileChange[];
  lastRun: AgentRunMetrics | null;
  /** The stored conversation this session is writing to, once there is one. */
  conversationId: string | null;
  /** True when a turn could not be stored — the panel says so rather than pretending. */
  unsaved: boolean;
  /** Answer the pending approval: once, for the rest of this run, or not at all. */
  approve: (decision: ApprovalDecision) => void;
  send: (text: string) => void;
  stop: () => void;
  reset: () => void;
  /** Start a fresh conversation; the previous one stays in history. */
  newConversation: () => void;
  /** Load a stored conversation and continue it. */
  resume: (id: string) => Promise<void>;
  /** Undo everything the last run wrote. */
  revertAll: () => void;
  /** Undo one file, leaving the rest of the run in place. */
  revert: (path: string) => void;
}

export function useAgentSession(opts: {
  siteId: string;
  siteName?: string;
  getOpenApi?: () => string | undefined;
}): AgentSession {
  const [entries, setEntries] = useState<AgentEntry[]>([]);
  const [running, setRunning] = useState(false);
  const [mode, setMode] = useState<AiMode>(DEFAULT_MODE);
  const [needsKey, setNeedsKey] = useState(false);
  const [keyProvider, setKeyProvider] = useState<string | null>(null);
  const [pending, setPending] = useState<PendingApproval | null>(null);
  const [scope, setScopeState] = useState<AgentScope>(DEFAULT_SCOPE);
  const [classification, setClassification] = useState<Classification | null>(null);
  const [changes, setChanges] = useState<FileChange[]>([]);
  const [lastRun, setLastRun] = useState<AgentRunMetrics | null>(null);
  const [conversationId, setConversationId] = useState<string | null>(null);
  const [unsaved, setUnsaved] = useState(false);

  const queryClient = useQueryClient();
  /** The stored conversation, read from inside the async loop where state lags a render. */
  const conversationRef = useRef<string | null>(null);
  /** The highest `Seq` the server has assigned, so a run can name the turns it produced. */
  const lastSeqRef = useRef(0);
  const messagesRef = useRef<Message[]>([]);
  const abortRef = useRef<AbortController | null>(null);
  const approveRef = useRef<((decision: ApprovalDecision) => void) | null>(null);
  const txRef = useRef<ReturnType<typeof createTransaction> | null>(null);
  const modeRef = useRef<AiMode>(mode);
  modeRef.current = mode;
  const scopeRef = useRef<AgentScope>(scope);
  scopeRef.current = scope;
  const permissions = usePermissions();
  // Written from inside the async run loop, where state would always be a render behind.
  const outcomeRef = useRef<AgentRunMetrics['outcome']>('completed');
  const modelRef = useRef<string | null>(null);
  /** The deterministic gate's verdict, or null when no check was possible. */
  const validationRef = useRef<{ ok: boolean; report: string } | null>(null);
  const classificationRef = useRef<Classification | null>(null);

  const push = useCallback((e: AgentEntry) => setEntries((prev) => [...prev, e]), []);

  const approve = useCallback((decision: ApprovalDecision) => {
    setPending(null);
    approveRef.current?.(decision);
    approveRef.current = null;
  }, []);

  const stop = useCallback(() => {
    abortRef.current?.abort();
    // Stopping mid-question is a "no". Resolving it as anything else would let the run press on
    // with a change nobody agreed to, which is the opposite of what the button says.
    approveRef.current?.('deny');
    approveRef.current = null;
    setPending(null);
  }, []);

  const reset = useCallback(() => {
    stop();
    messagesRef.current = [];
    setEntries([]);
    setChanges([]);
    setClassification(null);
    setNeedsKey(false);
  }, [stop]);

  /**
   * Start a new conversation.
   *
   * <p>Separate from `reset` on purpose: `reset` clears what is on screen, and this also
   * detaches from the stored conversation so the next turn opens a new one. Clearing the
   * transcript without detaching would keep appending a fresh discussion onto an old record.</p>
   */
  const newConversation = useCallback(() => {
    reset();
    conversationRef.current = null;
    lastSeqRef.current = 0;
    setConversationId(null);
    setUnsaved(false);
  }, [reset]);

  /**
   * Continue a stored conversation.
   *
   * <p>The stored blocks go back into the model's history verbatim — that is the whole point of
   * storing the Anthropic wire format — while the panel's entries are projected from them for
   * display. The two are different shapes of the same turns and neither is derived from the
   * other at render time.</p>
   */
  const resume = useCallback(
    async (id: string) => {
      reset();
      const detail = await queryClient.fetchQuery({
        queryKey: [conversationsKey, 'detail', id],
        staleTime: Infinity,
        queryFn: () =>
          api.get<{
            messages: { seq: number; role: 'user' | 'assistant'; content: unknown }[];
          }>(`/admin/ai/conversations/${id}`),
      });

      messagesRef.current = detail.messages.map(
        (m) => ({ role: m.role, content: m.content }) as Message,
      );
      lastSeqRef.current = detail.messages.reduce((max, m) => Math.max(max, m.seq), 0);
      conversationRef.current = id;
      setConversationId(id);
      setUnsaved(false);
      setEntries(projectEntries(messagesRef.current));
    },
    [queryClient, reset],
  );

  /** The pre-run type-problem snapshot the gate diffs against. See `send`. */
  const baselineRef = useRef<Promise<ReadonlySet<string> | null>>(Promise.resolve(null));

  const revertAll = useCallback(() => {
    txRef.current?.revertAll();
    setChanges([]);
  }, []);

  /**
   * Undo one file.
   *
   * <p>The change list is re-read from the transaction afterwards rather than filtered here:
   * the transaction is what knows whether the revert actually happened — a file the author has
   * since edited themselves is a case only it can see.</p>
   */
  const revert = useCallback((path: string) => {
    const tx = txRef.current;
    if (!tx) return;
    tx.revert(path);
    setChanges(tx.changes());
  }, []);

  const send = useCallback(
    (text: string) => {
      const trimmed = text.trim();
      if (!trimmed || running) return;

      setNeedsKey(false);
      push({ id: uid(), kind: 'user', text: trimmed });

      const controller = new AbortController();
      abortRef.current = controller;
      setRunning(true);

      const metrics = startRun();
      outcomeRef.current = 'completed';
      modelRef.current = null;
      // Generated here so the opening and closing halves of the run record are the same row.
      // See `recordRun`: a tab that dies mid-run must leave a run marked unfinished, not
      // nothing at all.
      const runId = uid();
      validationRef.current = null;
      const vfs = useVfs.getState();
      const tx = createTransaction(vfsPort());
      txRef.current = tx;

      const context: TenantToolContext = {
        workspace: currentWorkspace(opts.siteId),
        tx,
        index: currentIndex(),
        siteId: opts.siteId,
        branch: vfs.branch,
        // Tenant tools were written for the console dock and take its context. Attachments are
        // a dock concept with no IDE equivalent, so the IDE supplies an empty pool rather than
        // a different shape the sixteen tools would each have to handle.
        assistant: { attachments: [], onUploaded: () => {} },
      };

      /*
       * Hold autosave for the whole run.
       *
       * Released in the `finally` below, including on a throw or an abort — a run that dies
       * halfway has still written files, and leaving the hold on would strand them unsaved.
       */
      vfs.beginAgentRun();

      /*
       * What was already wrong, before this run touched anything.
       *
       * Started here and deliberately NOT awaited: the first model call takes seconds, and the
       * type worker answers while it is in flight, so on any run that does real work the
       * baseline is free. It has to be taken now — by the time the gate runs, the files it
       * would measure have already been edited.
       */
      baselineRef.current = baselineTypeProblems(Object.keys(vfs.files));

      /*
       * How much of the transcript has reached the server.
       *
       * <p>`messagesRef.current` is the live array the runtime appends to — the user turn, each
       * assistant reply, each batch of tool results — so at any instant `slice(persistedUpTo)`
       * is exactly what is new. Counting rather than copying is what keeps this correct without
       * the session having to know the loop's internal ordering.</p>
       */
      let persistedUpTo = messagesRef.current.length;
      const firstTurn = persistedUpTo === 0;

      /*
       * Store whatever is new, at every turn boundary rather than once at the end.
       *
       * <p><b>This is the whole of what makes a browser-side loop (D1) survivable.</b> Close the
       * tab mid-run and the server holds everything up to the last completed turn. Saving only
       * at the end would lose exactly the runs worth reading — the ones that crashed.</p>
       *
       * <p>Serialised through a promise chain because `Seq` is assigned server-side and two
       * overlapping appends would interleave the transcript. `persistedUpTo` advances only on
       * success, so a failed flush re-sends the same turns on the next boundary instead of
       * leaving a hole.</p>
       *
       * <p>Never throws. A turn that could not be stored sets `unsaved`, which the panel shows;
       * interrupting an answer the operator is reading to report a history-server problem would
       * be the wrong trade.</p>
       */
      let flushing: Promise<void> = Promise.resolve();
      const flush = (title?: string): Promise<void> => {
        let carryTitle = title;
        flushing = flushing.then(async () => {
          for (;;) {
            const id = conversationRef.current;
            const pending = nextBatch(messagesRef.current, persistedUpTo);
            if (!id || pending.length === 0) return;
            try {
              const { lastSeq } = await appendMessages(id, pending, {
                mode: modeRef.current,
                branch: useVfs.getState().branch,
                ...(carryTitle ? { title: carryTitle } : {}),
              });
              persistedUpTo += pending.length;
              carryTitle = undefined;
              lastSeqRef.current = lastSeq;
              setUnsaved(false);
              void queryClient.invalidateQueries({ queryKey: [conversationsKey] });
            } catch {
              // Stop on the first failure. `persistedUpTo` has not moved, so the next boundary
              // starts again from the same turn rather than leaving a hole in the transcript.
              setUnsaved(true);
              return;
            }
          }
        });
        return flushing;
      };

      void (async () => {
        let assistantId: string | null = null;
        let assistantText = '';
        let thinkingId: string | null = null;
        let thinkingText = '';
        let runFromSeq = 0;

        /*
         * The reducer, and the state it keeps, ahead of the run that drives it.
         *
         * <p><b>Order matters here and the compiler will not tell you.</b> `applyEvent` is a
         * function declaration, so it hoists and can be called from the loop below whatever
         * line it is written on; `toolEntries` is a `const`, which hoists into a temporal
         * dead zone instead. Declared after the loop, every `tool.started` threw
         * "Cannot access 'toolEntries' before initialization" — at runtime, in the browser,
         * on the first tool call of every run.</p>
         */

        /** The entry id for each in-flight tool call, so its result lands on its own card. */
        const toolEntries = new Map<string, string>();
        let checkId: string | null = null;

        function update(id: string, patch: (entry: AgentEntry) => AgentEntry): void {
          setEntries((prev) => prev.map((e) => (e.id === id ? patch(e) : e)));
        }

        function applyEvent(event: AgentEvent): void {
          switch (event.type) {
            case 'agent.thinking':
              if (thinkingId === null) {
                thinkingId = uid();
                thinkingText = event.delta;
                push({ id: thinkingId, kind: 'thinking', text: thinkingText });
              } else {
                thinkingText += event.delta;
                const id = thinkingId;
                update(id, (e) => ({ ...e, text: thinkingText }) as AgentEntry);
              }
              break;

            case 'text.delta':
              if (assistantId === null) {
                assistantId = uid();
                assistantText = event.delta;
                push({ id: assistantId, kind: 'assistant', text: assistantText });
              } else {
                assistantText += event.delta;
                const id = assistantId;
                update(id, (e) => ({ ...e, text: assistantText }) as AgentEntry);
              }
              break;

            case 'turn.completed':
              // A new turn starts a new bubble rather than appending to the last.
              assistantId = null;
              assistantText = '';
              thinkingId = null;
              thinkingText = '';
              recordTurn(metrics, event.usage);
              modelRef.current ??= event.model;
              // Stores the question, and on later turns the previous round's tool results.
              // The assistant turn itself is pushed by the runtime just after this event, so
              // it lands on the next boundary — which is what the next flush is for.
              void flush(firstTurn ? titleFrom(trimmed) : undefined);
              break;

            case 'tool.started': {
              recordToolCall(metrics, event.call.name);
              const id = uid();
              toolEntries.set(event.call.id, id);
              push({
                id,
                kind: 'tool',
                name: event.call.name,
                input: event.call.input ?? {},
                label: event.label,
                status: 'running',
                startedAt: Date.now(),
              });
              break;
            }

            case 'tool.completed': {
              const id = toolEntries.get(event.result.callId);
              if (id) {
                update(id, (e) =>
                  e.kind === 'tool'
                    ? {
                        ...e,
                        status: event.result.isError ? 'error' : 'ok',
                        result: event.result.content,
                        endedAt: Date.now(),
                      }
                    : e,
                );
                toolEntries.delete(event.result.callId);
              }
              // A tool round trip is the longest gap between turns and the likeliest moment for
              // a tab to be closed, so it is a boundary worth storing at.
              void flush();
              break;
            }

            case 'tool.declined': {
              const id = toolEntries.get(event.callId);
              if (id) {
                update(id, (e) => (e.kind === 'tool' ? { ...e, status: 'declined' } : e));
                toolEntries.delete(event.callId);
              }
              break;
            }

            case 'file.changed': {
              // Attached to whichever call is still open, so a card can say which files it
              // wrote. The runtime emits these while the call that caused them is in flight.
              const [openId] = [...toolEntries.values()].slice(-1);
              if (openId) {
                update(openId, (e) =>
                  e.kind === 'tool'
                    ? { ...e, paths: [...new Set([...(e.paths ?? []), event.path])] }
                    : e,
                );
              }
              break;
            }

            case 'validation.started':
              checkId = uid();
              push({ id: checkId, kind: 'check', status: 'running' });
              void flush();
              break;

            case 'validation.passed':
              validationRef.current = { ok: true, report: '' };
              if (checkId) update(checkId, (e) => (e.kind === 'check' ? { ...e, status: 'passed' } : e));
              checkId = null;
              break;

            case 'validation.failed': {
              // `problems: 0` is the runtime's way of saying no check was possible at all —
              // the preview was closed. Recording that as a failure would make an unverified
              // run read as a broken one, so it stays null: never checked, and the card says
              // "skipped" rather than claiming the build is broken.
              const skipped = event.problems === 0;
              validationRef.current = skipped ? null : { ok: false, report: 'The build failed.' };
              if (checkId) {
                update(checkId, (e) =>
                  e.kind === 'check' ? { ...e, status: skipped ? 'skipped' : 'failed' } : e,
                );
              }
              checkId = null;
              break;
            }

            case 'agent.retrying':
              push({
                id: uid(),
                kind: 'retry',
                attempt: event.attempt,
                of: event.of,
                because: event.because,
              });
              break;

            case 'agent.failed':
              outcomeRef.current = 'failed';
              push({ id: uid(), kind: 'error', text: event.message });
              break;

            case 'agent.stopped':
              outcomeRef.current = 'stopped';
              push({ id: uid(), kind: 'error', text: 'Stopped.' });
              break;

            default:
              break;
          }
        }

        try {
          /*
           * Open the conversation before the first model call, not after it.
           *
           * A conversation created only once a turn has succeeded would lose exactly the runs
           * worth keeping: the ones where the first call failed.
           */
          if (!conversationRef.current) {
            const created = await api.post<{ id: string }>('/admin/ai/conversations', {
              title: titleFrom(trimmed),
              mode: modeRef.current,
              surface: 'ide',
              siteId: opts.siteId,
              branch: vfs.branch,
            });
            conversationRef.current = created.id;
            setConversationId(created.id);
          }

          runFromSeq = lastSeqRef.current + 1;

          // The opening half of the run record. Written before the first model call so that a
          // run which never returns still leaves a row saying what was asked — with no
          // `finishedAt`, which is the honest reading of a tab that was closed.
          void recordRun(conversationRef.current, runId, {
            task: trimmed,
            fromSeq: runFromSeq,
            outcome: 'stopped',
            changes: [],
          });

          for await (const event of runAgent<TenantToolContext>({
            task: trimmed,
            messages: messagesRef.current,
            system: buildSystemPrompt({ siteName: opts.siteName, openApi: opts.getOpenApi?.() }),
            tools: toolsFor(
              [
                ...(ALL_TOOLS as ToolSpec<TenantToolContext>[]),
                ...SKILL_TOOLS,
                ...SANDBOX_TOOLS,
                ...TENANT_TOOLS,
              ],
              scopeRef.current,
              permissions,
            ),
            context,
            mode: modeRef.current,
            signal: controller.signal,
            transport: streamAssistantTurn,
            fileCount: Object.keys(vfs.files).length,
            /*
             * The deterministic gate. Runs only after a turn that wrote files and then stopped.
             *
             * Two checks, because they catch different things. esbuild proves the project
             * BUNDLES; it strips types without reading them, so a call with the wrong arguments
             * or a misspelled prop sails straight through it — and those are most of what an
             * agent actually gets wrong. TypeScript proves it TYPE-CHECKS, and does so whether
             * or not the preview is open.
             *
             * Type problems are diffed against a baseline taken at run start, so the agent is
             * judged on what it broke rather than on what it inherited. Without that, a site
             * carrying errors its authors chose to live with would fail every run and burn all
             * three repair turns on somebody else's code.
             *
             * `combineVerdict` returns null only when NEITHER check could run; the run is then
             * reported as unverified rather than as passing, which is the honest outcome and the
             * one the panel can surface.
             */
            validate: async () => {
              const changed = tx.changes().map((c) => c.path);

              const found = await checkTypes(changed);
              const baseline = await baselineRef.current;
              // A baseline that could not be taken makes the diff meaningless, so types go back
              // to "unchecked" rather than being reported wholesale against this run.
              const types = found === null || baseline === null ? null : newProblems(found, baseline);

              const snapshot = previewAvailable()
                ? await requestBuild(useVfs.getState().rev)
                : null;
              const build =
                snapshot && snapshot.revision !== -1
                  ? { ok: snapshot.ok, report: formatProblems(snapshot.problems) }
                  : null;

              return combineVerdict(types, build);
            },
            onClassified: (c) => {
              classificationRef.current = c;
              setClassification(c);
            },
            approve: (calls) =>
              new Promise<ApprovalDecision>((resolve) => {
                approveRef.current = resolve;
                setPending({ tools: calls });
              }),
            modelParams: {
              max_tokens: MAX_TOKENS,
              thinking: { type: 'adaptive' },
            },
          })) {
            applyEvent(event);
          }
        } catch (error) {
          if (error instanceof NoApiKeyError) {
            setNeedsKey(true);
            setKeyProvider(error.provider);
          }
          push({
            id: uid(),
            kind: 'error',
            text: (error as Error)?.message ?? 'The agent failed.',
          });
        } finally {
          const fileChanges = tx.changes();
          const run = finishRun(metrics, outcomeRef.current, { model: modelRef.current });
          setChanges(fileChanges);
          setLastRun(run);
          vfs.endAgentRun();
          setRunning(false);
          abortRef.current = null;

          // The last turns, then the closing half of the run record — in that order, so the
          // seq range the run claims is one the transcript actually contains.
          void flush(firstTurn ? titleFrom(trimmed) : undefined).then(() => {
            const id = conversationRef.current;
            if (!id) return;
            void recordRun(id, runId, {
              fromSeq: runFromSeq,
              toSeq: lastSeqRef.current,
              outcome: outcomeRef.current,
              complexity: classificationRef.current?.complexity ?? null,
              changes: fileChanges.map((c) => ({ path: c.path, kind: c.kind })),
              validation: validationRef.current,
              metrics: { ...run },
              finished: true,
            });
          });
        }
      })();
    },
    // `permissions` matters: it decides which tools the run is offered, and a stale copy would
    // hand the agent the tool set from before somebody's role changed.
    [opts, push, running, permissions, queryClient],
  );

  return {
    entries,
    running,
    mode,
    setMode,
    scope,
    setScope: (next: AgentScope) => {
      rememberScope(next);
      setScopeState(next);
    },
    needsKey,
    keyProvider,
    pending,
    classification,
    changes,
    lastRun,
    conversationId,
    unsaved,
    approve,
    send,
    stop,
    reset,
    newConversation,
    resume,
    revertAll,
    revert,
  };
}

function uid(): string {
  return crypto.randomUUID();
}

/**
 * As many unstored turns as will fit in one append.
 *
 * <p>The server caps an append at 1 MB, and a backlog can genuinely reach that: each failed
 * flush leaves its turns pending, so an outage during a long tool-heavy run accumulates them
 * until one request would carry the lot and be refused for being too big — at which point the
 * transcript would be stuck permanently rather than merely behind.</p>
 *
 * <p>So a batch is bounded by size and drained in a loop. <b>A single turn is always sent even
 * when it alone exceeds the budget</b>, because the alternative is skipping it: the Messages API
 * requires every `tool_use` to be answered by its `tool_result`, so a transcript missing one
 * message in the middle is one the provider refuses outright on resume. An oversized turn that
 * the server will not take stays unsaved and visibly so, which is the honest failure.</p>
 */
const MAX_APPEND_CHARS = 512 * 1024;

function nextBatch(messages: readonly Message[], from: number): Message[] {
  const batch: Message[] = [];
  let size = 0;

  for (let i = from; i < messages.length; i++) {
    const message = messages[i];
    const cost =
      typeof message.content === 'string' ? message.content.length : JSON.stringify(message.content).length;
    if (batch.length > 0 && size + cost > MAX_APPEND_CHARS) break;
    batch.push(message);
    size += cost;
  }

  return batch;
}

/**
 * Turn stored turns back into panel entries.
 *
 * <p>The model's history and the panel's transcript are two shapes of the same turns, and
 * neither is derived from the other while a run is live — the panel is built from streamed
 * deltas, the history from finished blocks. On resume there are no deltas to replay, so this
 * projects the blocks once.</p>
 *
 * <p>Tool results are shown as the call that produced them rather than as their content. A
 * resumed transcript that pasted whole file reads back into the panel would be unreadable, and
 * the detail is a click away in the stored conversation either way.</p>
 */
function projectEntries(messages: readonly Message[]): AgentEntry[] {
  const entries: AgentEntry[] = [];

  for (const message of messages) {
    if (typeof message.content === 'string') {
      entries.push({
        id: uid(),
        kind: message.role === 'user' ? 'user' : 'assistant',
        text: message.content,
      });
      continue;
    }

    for (const block of message.content as { type: string; [k: string]: unknown }[]) {
      if (block.type === 'text') {
        const text = String(block.text ?? '').trim();
        if (text) {
          entries.push({
            id: uid(),
            kind: message.role === 'user' ? 'user' : 'assistant',
            text,
          });
        }
      } else if (block.type === 'tool_use') {
        entries.push({
          id: uid(),
          kind: 'tool',
          name: String(block.name ?? 'tool'),
          input: (block.input as Record<string, unknown>) ?? {},
          label: String(block.name ?? 'tool'),
          // A stored call is over by definition, and the transcript does not record whether it
          // failed on the call itself — the failing result is the next block. `ok` here means
          // "it ran", and the error case below overrides it.
          status: 'ok',
          startedAt: 0,
        });
      } else if (block.type === 'tool_result' && block.is_error) {
        const last = entries[entries.length - 1];
        if (last?.kind === 'tool') {
          entries[entries.length - 1] = {
            ...last,
            status: 'error',
            result: String(block.content ?? 'failed'),
          };
        }
      }
    }
  }

  return entries;
}
