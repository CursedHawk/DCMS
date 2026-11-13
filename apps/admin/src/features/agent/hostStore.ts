import { useEffect } from 'react';
import { create } from 'zustand';
import type { MyPermissions } from '@dcms/core';
import type { Message } from '../ide/agent/client';
import type { AgentAnswers, AgentQuestion } from './ask';
import type { AgentRunMetrics } from '../ide/agent/runMetrics';
import type { SiteAgentKind } from '../ide/agent/systemPrompt';
import type { Classification } from './classify';
import type { ApprovalDecision, ToolCall } from './contracts';
import { DEFAULT_MODE, type AiMode } from './modes';
import { DEFAULT_SCOPE, type AgentScope } from './scope';
import type { SiteDraft } from './siteDraft';
import type { AgentTransaction, FileChange } from './transaction';

/**
 * The agent host: every site agent in this tab, independent of what is on screen (ADR 0033).
 *
 * <p>A site agent used to be a React hook inside the editor's panel. Leaving the page left its
 * loop running with nothing to show it or stop it; coming back and resuming the conversation
 * started a second loop on the same transcript; and after a site switch the orphan wrote into
 * whichever site the editor had loaded next. The loop now lives here, in a store that lasts as
 * long as the tab, keyed by site. The panel is a view of it, and so is the Agents tab in the
 * assistant dock.</p>
 *
 * <h3>Rules</h3>
 * <ul>
 *   <li><b>One run per site at a time.</b> Two agents on one draft step on each other's design
 *   even when per-file conflict checks keep them from overwriting each other's text. Anything
 *   else asked of a busy site queues behind the run.</li>
 *   <li><b>Runs on different sites are independent</b>, each over its own copy of its site
 *   (`siteDraft.ts`).</li>
 *   <li><b>Closing the tab ends every run</b> (D1 stands: the loop is in the browser). The tab
 *   asks first while one is running, as it would for unsaved work.</li>
 * </ul>
 */

/** One thing that happened in a run, as the panel shows it. */
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
  | { id: string; kind: 'error'; text: string }
  | { id: string; kind: 'notice'; text: string }
  /**
   * Where the conversation was compacted (ADR 0034): what came before is summarized here, and
   * archived in the knowledge store at `archivedAt` when it could be. `pending` while summarizing.
   */
  | { id: string; kind: 'compaction'; summary: string; archivedAt: string | null; saved: string[]; pending?: boolean };

export interface PendingApproval {
  tools: readonly ToolCall[];
}

/** Questions the agent is waiting on (`ask_user`). */
export interface PendingQuestion {
  id: string;
  questions: AgentQuestion[];
}

/** How a run ended. `paused` ran out of turns mid-task; the run record stores it as stopped. */
export type RunOutcome = 'completed' | 'paused' | 'failed' | 'stopped';

/** A task waiting for the site's current run to finish. */
export interface QueuedTask {
  requestId: string | null;
  task: string;
  origin: 'user' | 'delegated';
  requestedBy: string | null;
}

export interface SiteSession {
  /** The id the editor uses for the site: a site's id, or `repo:<id>` for a repository. */
  key: string;
  siteName?: string;
  kind: SiteAgentKind;
  /** A repository's Functions app, for the functions kind. */
  functionsApi?: string;
  entries: AgentEntry[];
  running: boolean;
  mode: AiMode;
  scope: AgentScope;
  needsKey: boolean;
  keyProvider: string | null;
  pending: PendingApproval | null;
  /** Questions waiting for an answer; the run is paused on them. */
  question: PendingQuestion | null;
  classification: Classification | null;
  /** Files the last run touched, for the change-review pane. */
  changes: FileChange[];
  lastRun: AgentRunMetrics | null;
  conversationId: string | null;
  /** True when a turn could not be stored — the panel says so rather than pretending. */
  unsaved: boolean;
  /** What the current (or last) run was asked. */
  task: string | null;
  origin: 'user' | 'delegated';
  /** The assistant conversation that asked for this run, when it was delegated. */
  requestedBy: string | null;
  startedAt: number | null;
  turns: number;
  /** What the run is doing right now, for a one-line status. */
  step: string | null;
  outcome: RunOutcome | null;
  queue: QueuedTask[];
}

/** A change the console assistant asked a site agent for, and how it went. */
export interface DelegatedRequest {
  id: string;
  siteKey: string;
  siteName?: string;
  task: string;
  requestedBy: string | null;
  status: 'queued' | 'running' | RunOutcome;
  conversationId: string | null;
  changes: string[];
  validation: { ok: boolean; report: string } | null;
  /** The agent's last words, once it has finished. */
  summary: string | null;
  at: number;
}

interface HostState {
  sessions: Record<string, SiteSession>;
  requests: Record<string, DelegatedRequest>;
  /** A site whose editor should show its agent when it next renders ("Open in editor"). */
  focus: string | null;
  /** A site agent the dock's Agents tab should open, when a notification was clicked. */
  dockAgent: string | null;
}

export const useAgentHost = create<HostState>(() => ({ sessions: {}, requests: {}, focus: null, dockAgent: null }));

/** Live parts of a session no component renders. */
interface Internals {
  messages: Message[];
  lastSeq: number;
  abort: AbortController | null;
  approve: ((decision: ApprovalDecision) => void) | null;
  answer: ((answers: AgentAnswers) => void) | null;
  /** Questions wait their turn: reads run in parallel, and two `ask_user` calls may arrive together. */
  asking: Promise<unknown>;
  tx: AgentTransaction | null;
  draft: SiteDraft | null;
  /** The last seq a compaction of this conversation covered, so the next archive starts after it. */
  compactedThrough: number;
}

const internals = new Map<string, Internals>();

export function inner(key: string): Internals {
  let found = internals.get(key);
  if (!found) {
    found = { messages: [], lastSeq: 0, abort: null, approve: null, answer: null, asking: Promise.resolve(), tx: null, draft: null, compactedThrough: 0 };
    internals.set(key, found);
  }
  return found;
}

function blank(key: string, kind: SiteAgentKind): SiteSession {
  return {
    key,
    kind,
    entries: [],
    running: false,
    mode: DEFAULT_MODE,
    // Mode A and Mode D start a step wider: their pages name plugin instances, and at site-only
    // scope the model cannot learn which exist and invents a slug that renders empty.
    scope: kind === 'react' || kind === 'functions' ? DEFAULT_SCOPE : 'site+tenant',
    needsKey: false,
    keyProvider: null,
    pending: null,
    question: null,
    classification: null,
    changes: [],
    lastRun: null,
    conversationId: null,
    unsaved: false,
    task: null,
    origin: 'user',
    requestedBy: null,
    startedAt: null,
    turns: 0,
    step: null,
    outcome: null,
    queue: [],
  };
}

/** The files a site's current (or last) run sees, for previewing what it proposes. */
export function runFiles(key: string): Readonly<Record<string, string>> {
  return internals.get(key)?.draft?.files() ?? {};
}

export function sessionOf(key: string): SiteSession | undefined {
  return useAgentHost.getState().sessions[key];
}

export function patch(key: string, changes: Partial<SiteSession> | ((s: SiteSession) => Partial<SiteSession>)): void {
  useAgentHost.setState((state) => {
    const current = state.sessions[key];
    if (!current) return state;
    const next = typeof changes === 'function' ? changes(current) : changes;
    return { sessions: { ...state.sessions, [key]: { ...current, ...next } } };
  });
}

export function patchRequest(id: string | null, changes: Partial<DelegatedRequest>): void {
  if (!id) return;
  useAgentHost.setState((state) => {
    const current = state.requests[id];
    if (!current) return state;
    return { requests: { ...state.requests, [id]: { ...current, ...changes } } };
  });
}

export function push(key: string, entry: AgentEntry): void {
  patch(key, (s) => ({ entries: [...s.entries, entry] }));
}

export function updateEntry(key: string, id: string, change: (entry: AgentEntry) => AgentEntry): void {
  patch(key, (s) => ({ entries: s.entries.map((e) => (e.id === id ? change(e) : e)) }));
}

/** Make sure a session exists for this site, and record what the page knows about it. */
export function configureSite(key: string, info: { kind: SiteAgentKind; siteName?: string; functionsApi?: string }): void {
  useAgentHost.setState((state) => {
    const current = state.sessions[key];
    if (
      current &&
      current.kind === info.kind &&
      current.siteName === (info.siteName ?? current.siteName) &&
      current.functionsApi === (info.functionsApi ?? current.functionsApi)
    ) {
      return state;
    }
    const base = current ?? blank(key, info.kind);
    return {
      sessions: {
        ...state.sessions,
        [key]: {
          ...base,
          kind: info.kind,
          siteName: info.siteName ?? base.siteName,
          functionsApi: info.functionsApi ?? base.functionsApi,
        },
      },
    };
  });
}

// ---------------------------------------------------------------------------
// Permissions: the host has no React context, so the shell hands them over.
// ---------------------------------------------------------------------------

let permissions: MyPermissions | undefined;

/** The member's permissions as last handed over, for filtering a run's tools. */
export function agentPermissions(): MyPermissions | undefined {
  return permissions;
}

/** Called by the app shell whenever the signed-in member's permissions load or change. */
export function setAgentPermissions(next: MyPermissions | undefined): void {
  permissions = next;
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

export function setSiteMode(key: string, mode: AiMode): void {
  patch(key, { mode });
}

export function setSiteScope(key: string, scope: AgentScope): void {
  patch(key, { scope });
}

/** Answer the pending approval: once, for the rest of this run, or not at all. */
export function approveSite(key: string, decision: ApprovalDecision): void {
  const i = inner(key);
  patch(key, { pending: null });
  i.approve?.(decision);
  i.approve = null;
}

/** Answer the agent's questions, or skip them with `null`. */
export function answerSite(key: string, answers: AgentAnswers): void {
  const i = inner(key);
  patch(key, { question: null });
  i.answer?.(answers);
  i.answer = null;
}

export function stopSite(key: string): void {
  const i = inner(key);
  i.abort?.abort();
  // Stopping mid-question is a "no": anything else would press on with a change nobody agreed to.
  i.approve?.('deny');
  i.approve = null;
  i.answer?.(null);
  i.answer = null;
  // Queued requests die with a stop; the assistant that asked is told so.
  for (const queued of sessionOf(key)?.queue ?? []) patchRequest(queued.requestId, { status: 'stopped' });
  patch(key, { pending: null, question: null, queue: [], step: null });
}

/** Clear the panel. Stops a run in progress: this is a person deliberately throwing it away. */
export function resetSite(key: string): void {
  stopSite(key);
  inner(key).messages = [];
  patch(key, { entries: [], changes: [], classification: null, needsKey: false });
}

/** Start a fresh conversation; the previous one stays in history. */
export function newSiteConversation(key: string): void {
  resetSite(key);
  inner(key).lastSeq = 0;
  patch(key, { conversationId: null, unsaved: false });
}

export function revertAllSite(key: string): void {
  inner(key).tx?.revertAll();
  patch(key, { changes: [] });
}

/** Undo one file. The transaction knows whether it could; the list is re-read from it. */
export function revertSite(key: string, path: string): void {
  const tx = inner(key).tx;
  if (!tx) return;
  tx.revert(path);
  patch(key, { changes: tx.changes() });
}

/**
 * Forget every session, stopping any run. For a change of workspace — a run must not carry one
 * tenant's conversation into another's — and for tests.
 */
export function resetAgentHost(): void {
  for (const key of Object.keys(useAgentHost.getState().sessions)) stopSite(key);
  internals.clear();
  useAgentHost.setState({ sessions: {}, requests: {}, focus: null, dockAgent: null });
}

// ---------------------------------------------------------------------------
// Closing the tab
// ---------------------------------------------------------------------------

/** True while this site's agent is working. Pages hold back their own rewrites meanwhile. */
export function useSiteAgentRunning(key: string | null | undefined): boolean {
  return useAgentHost((state) => (key ? state.sessions[key]?.running === true : false));
}

/** What every site agent in this tab is doing, for the assistant button's ring. */
export function useAgentActivity(): { working: number; waiting: boolean } {
  const working = useAgentHost((state) =>
    Object.values(state.sessions).filter((s) => s.running || s.queue.length > 0).length,
  );
  const waiting = useAgentHost((state) =>
    Object.values(state.sessions).some((s) => s.pending !== null || s.question !== null),
  );
  return { working, waiting };
}

/** Ask the editor for `key` to open its agent view once it is on screen. */
export function requestAgentFocus(key: string): void {
  useAgentHost.setState({ focus: key });
}

/** Opens the agent view when the Agents tab sent someone here to watch this site's agent. */
export function useAgentFocus(key: string, open: () => void): void {
  const wanted = useAgentHost((state) => state.focus === key);
  useEffect(() => {
    if (!wanted) return;
    open();
    useAgentHost.setState({ focus: null });
    // `open` is a fresh closure per render; the request is what matters.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [wanted]);
}

/** Open the dock's Agents tab on this site's agent (a notification was clicked). */
export function openAgentInDock(key: string): void {
  useAgentHost.setState({ dockAgent: key });
}

// ---------------------------------------------------------------------------
// Who is looking: a notification for an agent already on screen is noise.
// ---------------------------------------------------------------------------

const watched = new Map<string, number>();

/** Marks this site's agent as on screen while the calling component shows it. */
export function useWatchAgent(key: string | null | undefined, visible = true): void {
  useEffect(() => {
    if (!key || !visible) return;
    watched.set(key, (watched.get(key) ?? 0) + 1);
    return () => {
      const left = (watched.get(key) ?? 1) - 1;
      if (left > 0) watched.set(key, left);
      else watched.delete(key);
    };
  }, [key, visible]);
}

/** True when this site's agent is on screen in a visible tab. */
export function isAgentWatched(key: string): boolean {
  return watched.has(key) && (typeof document === 'undefined' || document.visibilityState === 'visible');
}

/** True while any site agent in this tab is working or has work queued. */
export function anySiteRunning(): boolean {
  return Object.values(useAgentHost.getState().sessions).some((s) => s.running || s.queue.length > 0);
}

if (typeof window !== 'undefined') {
  // Closing the tab ends every run (D1); the browser asks first, as for unsaved work.
  window.addEventListener('beforeunload', (event) => {
    if (!anySiteRunning()) return;
    event.preventDefault();
    event.returnValue = '';
  });
}

