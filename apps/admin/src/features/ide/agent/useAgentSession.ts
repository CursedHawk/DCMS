import { useEffect } from 'react';
import type { Classification } from '../../agent/classify';
import type { AgentAnswers } from '../../agent/ask';
import type { ApprovalDecision } from '../../agent/contracts';
import { resumeSite, sendToSite } from '../../agent/host';
import {
  type AgentEntry,
  answerSite,
  approveSite,
  configureSite,
  newSiteConversation,
  type PendingApproval,
  type PendingQuestion,
  resetSite,
  revertAllSite,
  revertSite,
  type RunOutcome,
  setSiteMode,
  setSiteScope,
  type SiteSession,
  stopSite,
  useAgentHost,
} from '../../agent/hostStore';
import { DEFAULT_MODE, type AiMode } from '../../agent/modes';
import { DEFAULT_SCOPE, rememberScope, type AgentScope } from '../../agent/scope';
import type { FileChange } from '../../agent/transaction';
import type { AgentRunMetrics } from './runMetrics';
import type { SiteAgentKind } from './systemPrompt';

export type { AgentEntry, PendingApproval };

/**
 * A site agent's session, as the panel sees it.
 *
 * <p>The run itself lives in the agent host (`features/agent/host.ts`, ADR 0033), so it keeps
 * going when this panel unmounts and is the same run when the panel comes back. This hook only
 * reads the host's session for its site and forwards commands to it.</p>
 */
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
  /** Questions the agent asked (`ask_user`), waiting for an answer. */
  question: PendingQuestion | null;
  /** How the last run was classified, so the panel can say why it did or did not plan. */
  classification: Classification | null;
  /** Files the last run touched, for the change-review pane. */
  changes: FileChange[];
  lastRun: AgentRunMetrics | null;
  /** How the last run ended — `paused` means it ran out of turns mid-task. */
  outcome: RunOutcome | null;
  /** Tasks waiting behind the current run, including ones the assistant asked for. */
  queued: number;
  /** The stored conversation this session is writing to, once there is one. */
  conversationId: string | null;
  /** True when a turn could not be stored — the panel says so rather than pretending. */
  unsaved: boolean;
  /** Answer the pending approval: once, for the rest of this run, or not at all. */
  approve: (decision: ApprovalDecision) => void;
  /** Answer the agent's questions, or skip them with `null`. */
  answer: (answers: AgentAnswers) => void;
  send: (text: string) => void;
  stop: () => void;
  reset: () => void;
  /** Start a fresh conversation; the previous one stays in history. */
  newConversation: () => void;
  /** Load a stored conversation and continue it. Refused while a run is going. */
  resume: (id: string) => Promise<void>;
  /** Undo everything the last run wrote. */
  revertAll: () => void;
  /** Undo one file, leaving the rest of the run in place. */
  revert: (path: string) => void;
}

const EMPTY: Omit<SiteSession, 'key' | 'kind'> = {
  entries: [],
  running: false,
  mode: DEFAULT_MODE,
  scope: DEFAULT_SCOPE,
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

export function useAgentSession(opts: {
  siteId: string;
  siteName?: string;
  /** Which kind of site this is; decides the prompt, the tools and the check (`agent/siteKits.ts`). */
  kind?: SiteAgentKind;
  /** A repository's Functions app, for the functions kind. */
  functionsApi?: string;
}): AgentSession {
  const key = opts.siteId;
  const kind: SiteAgentKind = opts.kind ?? 'react';

  useEffect(() => {
    configureSite(key, { kind, siteName: opts.siteName, functionsApi: opts.functionsApi });
  }, [key, kind, opts.siteName, opts.functionsApi]);

  const session = useAgentHost((state) => state.sessions[key]) ?? { ...EMPTY, key, kind };

  return {
    entries: session.entries,
    running: session.running,
    mode: session.mode,
    setMode: (mode) => setSiteMode(key, mode),
    scope: session.scope,
    setScope: (scope) => {
      rememberScope(scope);
      setSiteScope(key, scope);
    },
    needsKey: session.needsKey,
    keyProvider: session.keyProvider,
    pending: session.pending,
    question: session.question,
    classification: session.classification,
    changes: session.changes,
    lastRun: session.lastRun,
    outcome: session.outcome,
    queued: session.queue.length,
    conversationId: session.conversationId,
    unsaved: session.unsaved,
    approve: (decision) => approveSite(key, decision),
    answer: (answers) => answerSite(key, answers),
    send: (text) => {
      // The effect above may not have run yet on the very first render.
      configureSite(key, { kind, siteName: opts.siteName, functionsApi: opts.functionsApi });
      sendToSite(key, text);
    },
    stop: () => stopSite(key),
    reset: () => resetSite(key),
    newConversation: () => newSiteConversation(key),
    resume: (id) => resumeSite(key, id),
    revertAll: () => revertAllSite(key),
    revert: (path) => revertSite(key, path),
  };
}
