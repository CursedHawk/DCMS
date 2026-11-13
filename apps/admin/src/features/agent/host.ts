import { api } from '../../lib/api';
import { queryClient } from '../../lib/queryClient';
import {
  appendMessages,
  conversationsKey,
  recordRun,
  titleFrom,
} from '../assistant/conversations';
import { formatProblems } from '../ide/agent/checkTools';
import { type Message, NoApiKeyError, streamAssistantTurn } from '../ide/agent/client';
import { type AgentRunMetrics, finishRun, recordToolCall, recordTurn, startRun } from '../ide/agent/runMetrics';
import { buildSystemPrompt } from '../ide/agent/systemPrompt';
import type { TenantToolContext } from '../ide/agent/tenantTools';
import { combineVerdict, isCheckable, newProblems, problemKey } from '../ide/diagnostics/typeCheck';
import { clientId } from '../site-source/clientId';
import { languageOf } from '../site-source/paths';
import type { Classification } from './classify';
import { ASK_USER, type AgentAnswers, describeAsk, formatAnswers, parseQuestions } from './ask';
import type { AgentEvent, ApprovalDecision, ToolSpec } from './contracts';
import {
  type AgentEntry,
  agentPermissions,
  configureSite,
  type DelegatedRequest,
  inner,
  patch,
  patchRequest,
  push,
  type QueuedTask,
  type RunOutcome,
  sessionOf,
  updateEntry,
  useAgentHost,
} from './hostStore';
import { DEFAULT_MODE } from './modes';
import { createRunChecks } from './runChecks';
import { runAgent } from './runtime';
import { toolsFor } from './scope';
import { openSiteDraft, type SiteDraft } from './siteDraft';
import { kindOfRenderMode, kitFor } from './siteKits';
import { createTransaction, type AgentTransaction, type FileChange } from './transaction';

/**
 * Running site agents: the part of the host that does the work (ADR 0033).
 *
 * <p>Split from `hostStore.ts` so the app shell — the assistant button, the dock's Agents tab —
 * can show and steer runs without loading the agent runtime, the tool registry and the draft
 * machinery into every page. This module loads with the first editor, or the first delegation.</p>
 */

/**
 * Continue a stored conversation.
 *
 * <p>Refused while a run is going: replacing the transcript under a live loop is how the
 * HrSite conversation ended up with two loops interleaving one history.</p>
 */
export async function resumeSite(key: string, conversationId: string): Promise<void> {
  if (sessionOf(key)?.running) return;
  const detail = await queryClient.fetchQuery({
    queryKey: [conversationsKey, 'detail', conversationId],
    staleTime: Infinity,
    queryFn: () =>
      api.get<{ messages: { seq: number; role: 'user' | 'assistant'; content: unknown }[] }>(
        `/admin/ai/conversations/${conversationId}`,
      ),
  });
  if (sessionOf(key)?.running) return;
  const i = inner(key);
  i.messages = detail.messages.map((m) => ({ role: m.role, content: m.content }) as Message);
  i.lastSeq = detail.messages.reduce((max, m) => Math.max(max, m.seq), 0);
  patch(key, {
    conversationId,
    unsaved: false,
    entries: projectEntries(i.messages),
    changes: [],
    classification: null,
    needsKey: false,
  });
}

/** Ask the site's agent to do something. Queued if it is already busy. */
export function sendToSite(key: string, text: string): void {
  const task = text.trim();
  const session = sessionOf(key);
  if (!task || !session) return;
  if (session.running) {
    patch(key, (s) => ({ queue: [...s.queue, { requestId: null, task, origin: 'user', requestedBy: null }] }));
    return;
  }
  void run(key, { requestId: null, task, origin: 'user', requestedBy: null });
}

// ---------------------------------------------------------------------------
// Delegation from the console assistant
// ---------------------------------------------------------------------------

/**
 * The console assistant asking a site's own agent for a change.
 *
 * <p>The assistant never edits a site itself: the site's agent has the files, the compiler and
 * the specialised tools. The request runs in a conversation of its own, on the site's IDE
 * surface, so the author finds it in that site's agent history.</p>
 */
export async function requestSiteChange(input: {
  siteId: string;
  task: string;
  requestedBy: string | null;
}): Promise<DelegatedRequest> {
  const site = await api.get<{ name: string; renderMode: string }>(`/admin/sites/${input.siteId}`);
  const kind = kindOfRenderMode(site.renderMode);
  if (!kind) throw new Error(`${site.name} has no site agent: its files are uploaded, not edited.`);

  configureSite(input.siteId, { kind, siteName: site.name });
  const request: DelegatedRequest = {
    id: crypto.randomUUID(),
    siteKey: input.siteId,
    siteName: site.name,
    task: input.task,
    requestedBy: input.requestedBy,
    status: 'queued',
    conversationId: null,
    changes: [],
    validation: null,
    summary: null,
    at: Date.now(),
  };
  useAgentHost.setState((state) => ({ requests: { ...state.requests, [request.id]: request } }));

  const queued: QueuedTask = {
    requestId: request.id,
    task: input.task,
    origin: 'delegated',
    requestedBy: input.requestedBy,
  };
  if (sessionOf(input.siteId)?.running) {
    patch(input.siteId, (s) => ({ queue: [...s.queue, queued] }));
  } else {
    void run(input.siteId, queued);
  }
  return useAgentHost.getState().requests[request.id];
}

// ---------------------------------------------------------------------------
// The run
// ---------------------------------------------------------------------------

function uid(): string {
  return crypto.randomUUID();
}

/** The task as the model reads it. A delegated one says who asked, which changes how to report. */
function promptFor(queued: QueuedTask): string {
  if (queued.origin !== 'delegated') return queued.task;
  return `The workspace assistant asked for this change on behalf of the user:\n\n${queued.task}\n\nDo it, verify it, and end with a short summary of what you changed — the assistant reads that summary back to the user.`;
}

/** Why a run did not start because another agent holds the site, in this browser or elsewhere. */
const OTHER_TAB = 'Another tab in this browser already has an agent working on this site. Let it finish, or stop it there, then ask again.';
const ELSEWHERE = 'You already have an agent working on this site — in another tab or on another device. Let it finish, or stop it there, then ask again.';

/** Thrown inside a run when the server refuses its lease. */
class SiteBusyError extends Error {}

/** How often a running run tells the server it is alive (the lease lasts 60 s). */
const HEARTBEAT_MS = 20_000;

/**
 * Start a run, if no other tab of this browser is running one on the site.
 *
 * <p>The tab's own rule — one run per site — is the session's `running` flag, claimed here
 * before anything is awaited. Other tabs of the same browser are kept out by a Web Lock on the
 * site, held for the whole run and released by the browser when the tab goes; other devices by
 * the server's lease (see `runHeld`). A browser without Web Locks relies on the lease alone.</p>
 */
async function run(key: string, queued: QueuedTask): Promise<void> {
  const session = sessionOf(key);
  if (!session || session.running) return;
  patch(key, { running: true });

  const locks = typeof navigator !== 'undefined' ? navigator.locks : undefined;
  if (!locks) {
    await runHeld(key, queued);
  } else {
    const started = await locks.request(`dcms-site-agent:${key}`, { ifAvailable: true }, async (lock) => {
      if (!lock) return false;
      await runHeld(key, queued);
      return true;
    });
    if (!started) {
      refuse(key, queued, OTHER_TAB);
      return;
    }
  }

  // Only now: the lock is released once `request` settles, and the next run takes it afresh.
  const next = sessionOf(key)?.queue[0];
  if (next) {
    patch(key, (s) => ({ queue: s.queue.slice(1) }));
    void run(key, next);
  }
}

/** A run that could not start: say so, and let whatever was queued behind it go too. */
function refuse(key: string, queued: QueuedTask, message: string): void {
  push(key, { id: uid(), kind: 'user', text: queued.task });
  push(key, { id: uid(), kind: 'error', text: message });
  patchRequest(queued.requestId, { status: 'stopped', summary: message });
  for (const waiting of sessionOf(key)?.queue ?? []) patchRequest(waiting.requestId, { status: 'stopped', summary: message });
  patch(key, { running: false, queue: [], outcome: 'stopped' });
}

async function runHeld(key: string, queued: QueuedTask): Promise<void> {
  const session = sessionOf(key);
  if (!session) return;
  const i = inner(key);
  const prompt = promptFor(queued);

  // A delegated request is its own conversation: the author finds it in the site's history
  // under its own title, rather than appended to whatever they last discussed with the agent.
  if (queued.origin === 'delegated') {
    i.messages = [];
    i.lastSeq = 0;
    patch(key, { conversationId: null, entries: [], changes: [] });
  }

  const controller = new AbortController();
  i.abort = controller;
  const runId = uid();
  const metrics = startRun();
  // Typed by assertion: the event reducer below assigns it, which narrowing cannot see.
  let outcome = 'completed' as RunOutcome;
  let model: string | null = null;
  let validation: { ok: boolean; report: string } | null = null;
  let classification = null as Classification | null;

  patch(key, {
    running: true,
    task: queued.task,
    origin: queued.origin,
    requestedBy: queued.requestedBy,
    startedAt: Date.now(),
    turns: 0,
    step: null,
    outcome: null,
    needsKey: false,
    pending: null,
  });
  push(key, { id: uid(), kind: 'user', text: queued.origin === 'delegated' ? `Requested by the assistant: ${queued.task}` : queued.task });
  patchRequest(queued.requestId, { status: 'running' });

  /*
   * How much of the transcript has reached the server.
   *
   * `i.messages` is the live array the runtime appends to, so `slice(persistedUpTo)` is exactly
   * what is new. Stored at every turn boundary, not once at the end: close the tab mid-run and
   * the server holds everything up to the last completed turn. Serialised, because `Seq` is
   * assigned server-side and two overlapping appends would interleave the transcript.
   */
  let persistedUpTo = i.messages.length;
  const firstTurn = persistedUpTo === 0;
  let flushing: Promise<void> = Promise.resolve();
  const flush = (title?: string): Promise<void> => {
    let carryTitle = title;
    flushing = flushing.then(async () => {
      for (;;) {
        const id = sessionOf(key)?.conversationId;
        const pending = nextBatch(i.messages, persistedUpTo);
        if (!id || pending.length === 0) return;
        try {
          const { lastSeq } = await appendMessages(id, pending, {
            mode: sessionOf(key)?.mode ?? DEFAULT_MODE,
            branch: i.draft?.branch,
            ...(carryTitle ? { title: carryTitle } : {}),
          });
          persistedUpTo += pending.length;
          carryTitle = undefined;
          i.lastSeq = lastSeq;
          patch(key, { unsaved: false });
          void queryClient.invalidateQueries({ queryKey: [conversationsKey] });
        } catch {
          // `persistedUpTo` has not moved, so the next boundary resends rather than leaving a hole.
          patch(key, { unsaved: true });
          return;
        }
      }
    });
    return flushing;
  };

  let assistantId: string | null = null;
  let assistantText = '';
  let thinkingId: string | null = null;
  let thinkingText = '';
  let checkId: string | null = null;
  let runFromSeq = 0;
  /** Set when the server refused the lease: no run row exists, so nothing is closed. */
  let refused = false;
  /** Whether this run opened the conversation, so a refused one can take it away again. */
  let createdHere = false;
  let heartbeat: ReturnType<typeof setInterval> | undefined;
  /** The entry id for each in-flight tool call, so its result lands on its own card. */
  const toolEntries = new Map<string, string>();
  let tx: AgentTransaction | null = null;
  let checks: ReturnType<typeof createRunChecks> | null = null;

  function applyEvent(event: AgentEvent): void {
    switch (event.type) {
      case 'agent.thinking':
        if (thinkingId === null) {
          thinkingId = uid();
          thinkingText = event.delta;
          push(key, { id: thinkingId, kind: 'thinking', text: thinkingText });
        } else {
          thinkingText += event.delta;
          const text = thinkingText;
          updateEntry(key, thinkingId, (e) => ({ ...e, text }) as AgentEntry);
        }
        break;

      case 'text.delta':
        if (assistantId === null) {
          assistantId = uid();
          assistantText = event.delta;
          push(key, { id: assistantId, kind: 'assistant', text: assistantText });
        } else {
          assistantText += event.delta;
          const text = assistantText;
          updateEntry(key, assistantId, (e) => ({ ...e, text }) as AgentEntry);
        }
        break;

      case 'turn.completed':
        assistantId = null;
        assistantText = '';
        thinkingId = null;
        thinkingText = '';
        recordTurn(metrics, event.usage);
        model ??= event.model;
        patch(key, { turns: event.turn });
        void flush(firstTurn ? titleFrom(queued.task) : undefined);
        break;

      case 'tool.started': {
        recordToolCall(metrics, event.call.name);
        const id = uid();
        toolEntries.set(event.call.id, id);
        push(key, {
          id,
          kind: 'tool',
          name: event.call.name,
          input: event.call.input ?? {},
          label: event.label,
          status: 'running',
          startedAt: Date.now(),
        });
        patch(key, { step: event.label });
        break;
      }

      case 'tool.completed': {
        const id = toolEntries.get(event.result.callId);
        if (id) {
          updateEntry(key, id, (e) =>
            e.kind === 'tool'
              ? { ...e, status: event.result.isError ? 'error' : 'ok', result: event.result.content, endedAt: Date.now() }
              : e,
          );
          toolEntries.delete(event.result.callId);
        }
        if (toolEntries.size === 0) patch(key, { step: null });
        void flush();
        break;
      }

      case 'tool.declined': {
        const id = toolEntries.get(event.callId);
        if (id) {
          updateEntry(key, id, (e) => (e.kind === 'tool' ? { ...e, status: 'declined' } : e));
          toolEntries.delete(event.callId);
        }
        break;
      }

      case 'file.changed': {
        const [openId] = [...toolEntries.values()].slice(-1);
        if (openId) {
          updateEntry(key, openId, (e) =>
            e.kind === 'tool' ? { ...e, paths: [...new Set([...(e.paths ?? []), event.path])] } : e,
          );
        }
        if (tx) patch(key, { changes: tx.changes() });
        break;
      }

      case 'validation.started':
        checkId = uid();
        push(key, { id: checkId, kind: 'check', status: 'running' });
        patch(key, { step: 'checking' });
        void flush();
        break;

      case 'validation.passed':
        validation = { ok: true, report: '' };
        if (checkId) updateEntry(key, checkId, (e) => (e.kind === 'check' ? { ...e, status: 'passed' } : e));
        checkId = null;
        patch(key, { step: null });
        break;

      case 'validation.failed': {
        // `problems: 0` is "no check was possible", which is unverified — not broken.
        const skipped = event.problems === 0;
        validation = skipped ? null : { ok: false, report: 'The build failed.' };
        if (checkId) {
          updateEntry(key, checkId, (e) => (e.kind === 'check' ? { ...e, status: skipped ? 'skipped' : 'failed' } : e));
        }
        checkId = null;
        patch(key, { step: null });
        break;
      }

      case 'agent.retrying':
        push(key, { id: uid(), kind: 'retry', attempt: event.attempt, of: event.of, because: event.because });
        break;

      case 'agent.paused':
        outcome = 'paused';
        push(key, {
          id: uid(),
          kind: 'notice',
          text: `Paused after ${event.turns} turns without finishing. Say "continue" to carry on from here.`,
        });
        break;

      case 'agent.failed':
        outcome = 'failed';
        push(key, { id: uid(), kind: 'error', text: event.message });
        break;

      case 'agent.stopped':
        outcome = 'stopped';
        push(key, { id: uid(), kind: 'error', text: 'Stopped.' });
        break;

      default:
        break;
    }
  }

  try {
    const current = sessionOf(key)!;
    const kit = await kitFor(current.kind, { functionsApi: current.functionsApi });

    // The previous run's draft may still be saving a revert; it lands before this one loads.
    await i.draft?.flush();
    const draft = await openSiteDraft(key, { runId });
    i.draft = draft;
    tx = createTransaction(draft.port);
    i.tx = tx;
    checks = createRunChecks({ siteId: key, files: draft.files, lib: kit.typeLib });
    const runChecks = checks;

    const context: TenantToolContext = {
      // Getters: every tool call sees the files as they are now, including this run's own
      // writes. A workspace pinned at run start is what made the run edit lines it could not see.
      get workspace() {
        return draft.workspace();
      },
      get index() {
        return draft.index();
      },
      files: draft.files,
      tx,
      siteId: key,
      branch: draft.branch,
      checks: runChecks,
      flushDraft: draft.flush,
      // Tenant tools take the dock's context; the IDE has no attachments to offer.
      assistant: { attachments: [], onUploaded: () => {} },
    };

    /*
     * What was already wrong before this run touched anything, so the gate judges the run on
     * what it broke. Started now and not awaited: it runs while the first model call is out.
     */
    const baseline: Promise<ReadonlySet<string> | null> =
      current.kind !== 'react'
        ? Promise.resolve(null)
        : (() => {
            const checkable = Object.keys(draft.files()).filter(
              (p) => isCheckable(p) && languageOf(p) !== 'csharp',
            );
            if (checkable.length > 400) return Promise.resolve(null);
            return runChecks.types(checkable).then((found) => (found ? new Set(found.map(problemKey)) : null));
          })();

    // Open the conversation before the first model call: a run whose first call fails is
    // exactly the one worth keeping.
    if (!sessionOf(key)?.conversationId) {
      createdHere = true;
      const created = await api.post<{ id: string }>('/admin/ai/conversations', {
        title: titleFrom(queued.task),
        mode: current.mode,
        surface: 'ide',
        // A repository's conversations are keyed by its binding, as a site's by the site.
        siteId: key.replace(/^repo:/, ''),
        branch: draft.branch,
      });
      patch(key, { conversationId: created.id });
    }
    const conversationId = sessionOf(key)!.conversationId!;
    patchRequest(queued.requestId, { conversationId });

    runFromSeq = i.lastSeq + 1;
    // The opening half of the run record, with no `finishedAt` until the closing half: a tab
    // closed mid-run leaves a row saying what was asked. It is also the site agent lease: the
    // server refuses it while another tab or device of this member has an agent on this site.
    const opened = await recordRun(conversationId, runId, {
      task: queued.task,
      fromSeq: runFromSeq,
      outcome: 'stopped',
      changes: [],
      clientId: clientId(),
    });
    if (opened === 'busy') throw new SiteBusyError(ELSEWHERE);
    heartbeat = setInterval(() => void recordRun(conversationId, runId, { clientId: clientId() }), HEARTBEAT_MS);

    const tools = [...withDraftNotes(toolsFor(kit.tools, current.scope, agentPermissions()), draft), askTool(key)];

    for await (const event of runAgent<TenantToolContext>({
      task: prompt,
      messages: i.messages,
      system: buildSystemPrompt({ kind: current.kind, siteName: current.siteName }),
      tools,
      context,
      mode: current.mode,
      signal: controller.signal,
      transport: streamAssistantTurn,
      fileCount: Object.keys(draft.files()).length,
      /*
       * The deterministic gate, after a turn that wrote files and then stopped. A kind with a
       * gate of its own uses it; Mode B checks types (diffed against the baseline, so the run is
       * judged on what it broke) and the bundle, because each catches what the other cannot.
       * `null` means neither could run: reported as unverified, never as passing.
       */
      validate: async () => {
        await draft.flush();
        if (kit.validate) return kit.validate(draft.files());
        const changed = (tx?.changes() ?? []).map((c) => c.path).filter((p) => isCheckable(p) && languageOf(p) !== 'csharp');
        const found = changed.length > 0 ? await runChecks.types(changed) : [];
        const before = await baseline;
        const types = found === null || before === null ? null : newProblems(found, before);
        const built = await runChecks.build();
        return combineVerdict(types, built ? { ok: built.ok, report: formatProblems(built.problems) } : null);
      },
      onClassified: (c) => {
        classification = c;
        patch(key, { classification: c });
      },
      approve: (calls) =>
        new Promise<ApprovalDecision>((resolve) => {
          i.approve = resolve;
          patch(key, { pending: { tools: calls } });
        }),
    })) {
      applyEvent(event);
    }
  } catch (error) {
    if (error instanceof SiteBusyError) {
      refused = true;
      outcome = 'stopped';
      push(key, { id: uid(), kind: 'error', text: error.message });
      // A conversation opened for nothing would sit empty in the site's history.
      const id = sessionOf(key)?.conversationId;
      if (createdHere && id) {
        void api.del(`/admin/ai/conversations/${id}`).catch(() => undefined);
        patch(key, { conversationId: null });
      }
    } else if (controller.signal.aborted) {
      outcome = 'stopped';
    } else {
      outcome = 'failed';
      if (error instanceof NoApiKeyError) patch(key, { needsKey: true, keyProvider: error.provider });
      push(key, { id: uid(), kind: 'error', text: (error as Error)?.message ?? 'The agent failed.' });
    }
  } finally {
    clearInterval(heartbeat);
    await i.draft?.flush().catch(() => undefined);
    checks?.dispose();
    const fileChanges = tx?.changes() ?? [];
    const finished = finishRun(metrics, outcome === 'paused' ? 'stopped' : outcome, { model });
    if (i.abort === controller) i.abort = null;
    i.approve = null;
    i.answer?.(null);
    i.answer = null;
    patch(key, {
      running: false,
      pending: null,
      question: null,
      step: null,
      changes: fileChanges,
      lastRun: finished,
      outcome,
    });
    patchRequest(queued.requestId, {
      status: outcome,
      changes: fileChanges.map((c) => c.path),
      validation,
      summary: refused ? ELSEWHERE : lastAssistantText(i.messages),
    });
    if (refused) {
      // What was queued behind it would be refused the same way.
      for (const waiting of sessionOf(key)?.queue ?? []) patchRequest(waiting.requestId, { status: 'stopped', summary: ELSEWHERE });
      patch(key, { queue: [] });
    } else {
      // The last turns, then the closing half of the run record — in that order, so the seq
      // range the run claims is one the transcript actually contains.
      void closeRun(fileChanges, finished);
    }
  }

  async function closeRun(fileChanges: readonly FileChange[], finished: AgentRunMetrics): Promise<void> {
    await flush(firstTurn ? titleFrom(queued.task) : undefined);
    const id = sessionOf(key)?.conversationId;
    if (!id) return;
    await recordRun(id, runId, {
      fromSeq: runFromSeq,
      toSeq: i.lastSeq,
      // The server knows completed/failed/stopped. A paused run did not finish: stopped.
      outcome: outcome === 'paused' ? 'stopped' : outcome,
      complexity: classification?.complexity ?? null,
      changes: fileChanges.map((c) => ({ path: c.path, kind: c.kind })),
      validation,
      metrics: { ...finished },
      finished: true,
    });
  }
}

/**
 * `ask_user` for a site's run: the questions wait in the host store, where the panel, the dock's
 * Agents tab and the notifications all see them, until someone answers, skips or stops the run.
 */
function askTool(key: string): ToolSpec<TenantToolContext> {
  return {
    ...ASK_USER,
    describe: describeAsk,
    run: async (input) => {
      const questions = parseQuestions(input);
      if (typeof questions === 'string') return { content: questions, isError: true };
      const i = inner(key);
      const asked = i.asking.then(
        () =>
          new Promise<AgentAnswers>((resolve) => {
            // A second set queued behind one answered by a stop: nobody is left to ask.
            if (!i.abort || i.abort.signal.aborted) return resolve(null);
            i.answer = resolve;
            patch(key, { question: { id: uid(), questions } });
          }),
      );
      i.asking = asked;
      return { content: formatAnswers(questions, await asked) };
    },
  };
}

/**
 * The agent's last words, read from its stored turns rather than the streamed deltas: a turn can
 * arrive whole without streaming (a translated provider, a cached reply).
 */
function lastAssistantText(messages: readonly Message[]): string | null {
  for (let index = messages.length - 1; index >= 0; index--) {
    const message = messages[index];
    if (message.role !== 'assistant') continue;
    const text =
      typeof message.content === 'string'
        ? message.content
        : (message.content as { type: string; text?: string }[])
            .filter((b) => b.type === 'text')
            .map((b) => b.text ?? '')
            .join('');
    if (text.trim()) return text.trim();
  }
  return null;
}

/**
 * Tell the model about writes the server refused, on the next result it reads.
 *
 * <p>A save happens after the edit tool has already reported success, so a 409 has no result of
 * its own to land in. Without this the model would carry on believing a change that was thrown
 * away.</p>
 */
function withDraftNotes(
  tools: readonly ToolSpec<TenantToolContext>[],
  draft: SiteDraft,
): ToolSpec<TenantToolContext>[] {
  return tools.map((tool) => ({
    ...tool,
    run: async (input, ctx) => {
      const outcome = await tool.run(input, ctx);
      const notes = draft.takeNotes();
      if (notes.length === 0) return outcome;
      return { ...outcome, content: `${outcome.content}\n\n${notes.join('\n')}` };
    },
  }));
}

// ---------------------------------------------------------------------------
// Transcript helpers
// ---------------------------------------------------------------------------

/**
 * As many unstored turns as will fit in one append.
 *
 * <p>The server caps an append at 1 MB, and a backlog of failed flushes can reach that. A single
 * turn is always sent even when it alone exceeds the budget: the Messages API requires every
 * `tool_use` to be answered by its `tool_result`, so a transcript missing one message in the
 * middle is one the provider refuses outright on resume.</p>
 */
const MAX_APPEND_CHARS = 512 * 1024;

export function nextBatch(messages: readonly Message[], from: number): Message[] {
  const batch: Message[] = [];
  let size = 0;
  for (let index = from; index < messages.length; index++) {
    const message = messages[index];
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
 * <p>Tool results are shown as the call that produced them rather than as their content: a
 * resumed transcript that pasted whole file reads back into the panel would be unreadable.</p>
 */
export function projectEntries(messages: readonly Message[]): AgentEntry[] {
  const entries: AgentEntry[] = [];

  for (const message of messages) {
    if (typeof message.content === 'string') {
      entries.push({ id: uid(), kind: message.role === 'user' ? 'user' : 'assistant', text: message.content });
      continue;
    }

    for (const block of message.content as { type: string; [k: string]: unknown }[]) {
      if (block.type === 'text') {
        const text = String(block.text ?? '').trim();
        if (text) entries.push({ id: uid(), kind: message.role === 'user' ? 'user' : 'assistant', text });
      } else if (block.type === 'tool_use') {
        entries.push({
          id: uid(),
          kind: 'tool',
          name: String(block.name ?? 'tool'),
          input: (block.input as Record<string, unknown>) ?? {},
          label: block.name === ASK_USER.name ? describeAsk((block.input as Record<string, unknown>) ?? {}) : String(block.name ?? 'tool'),
          // A stored call is over by definition; a failing result below overrides this.
          status: 'ok',
          startedAt: 0,
        });
      } else if (block.type === 'tool_result' && block.is_error) {
        const last = entries[entries.length - 1];
        if (last?.kind === 'tool') {
          entries[entries.length - 1] = { ...last, status: 'error', result: String(block.content ?? 'failed') };
        }
      }
    }
  }

  return entries;
}
