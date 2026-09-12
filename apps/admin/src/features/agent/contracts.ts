import type { TurnUsage } from '../ide/agent/client';
import type { ToolRisk } from './modes';

/**
 * The vocabulary both agent surfaces speak.
 *
 * <p>There are two of them — the workspace assistant in the admin console and the coding agent
 * in the Mode B IDE — and under the rework they are two UIs over <b>one</b> runtime, one tool
 * registry and one mode model. That only holds if they agree on what a tool is, what an event
 * is, and what a workspace revision is; two surfaces each with their own near-identical types is
 * how they drift back apart.</p>
 *
 * <p>Nothing here talks to a server, imports a UI framework, or knows which surface it is
 * serving. That is deliberate: this module is the contract, and a contract that depends on one
 * of its implementations is not one.</p>
 */

// ---------------------------------------------------------------------------
// Tools
// ---------------------------------------------------------------------------

/**
 * What the model is told a tool is, plus what the app needs to run and narrate it.
 *
 * <p>Generalised from the console assistant's `AssistantTool`, which already had the shape
 * right; the type parameter is the only real addition. The two surfaces hand their tools very
 * different context — the console passes attachments, the IDE passes a workspace — and erasing
 * that difference behind `any` would put an untyped hole in the middle of the one abstraction
 * this rework is built on.</p>
 */
export interface ToolSpec<TContext> {
  name: string;
  description: string;
  /** JSON Schema, as the provider expects it. */
  input_schema: Record<string, unknown>;

  /**
   * The permission a caller must hold for this tool to be offered at all.
   *
   * <p>Absent from the list, not disabled in it. A model told a tool exists will reach for it,
   * and an assistant repeatedly announcing it cannot do what it just offered reads as broken
   * rather than careful. The server refuses independently — this stops the conversation going
   * somewhere it cannot end.</p>
   */
  permission?: string;

  /** How much damage it can do, for the mode table in `modes.ts`. Omitted means read-only. */
  risk?: ToolRisk;

  /** React-query key roots to invalidate once it has run. */
  invalidates?: string[];

  /** One line, future tense, for the approval card: "Publish 3 articles". */
  summarize?: (input: Record<string, unknown>) => string;

  /** One line, past tense, for the transcript: "Created draft autumn-26". */
  describe?: (input: Record<string, unknown>) => string;

  /**
   * The maximum size of result this tool may return, in characters.
   *
   * <p>A tool with no ceiling is how a run burns its whole budget on one directory listing. The
   * runtime truncates past this and appends a line saying how to get the rest, so the model
   * learns to narrow the request rather than silently receiving half an answer. Omitted means
   * the runtime's default applies.</p>
   */
  maxResultChars?: number;

  /**
   * Where this tool's output comes from, when that is somewhere outside the workspace.
   *
   * <p><b>The injection surface, named.</b> Most of what the agent reads was written by people
   * who can already edit this site — a colleague's code is not a threat the agent can defend
   * against and pretending otherwise is theatre. But some tool output carries text from people
   * with no access at all: a form submission is written by any visitor on the internet, and the
   * rendered page contains whatever the site chose to display.</p>
   *
   * <p>Results from those tools are wrapped by the runtime with a line naming the source, so
   * the boundary between "the model's instructions" and "text the model is looking at" is in
   * the transcript rather than only in the system prompt. See `docs/ai-agent.md`.</p>
   *
   * <p><b>This is mitigation, not protection.</b> What actually stops an injected instruction
   * is that the agent holds no authority the user does not: dangerous tools need approval, the
   * scope axis bounds reach, and the server checks every permission again regardless of what
   * the model believed.</p>
   */
  untrustedSource?: string;

  run: (input: Record<string, unknown>, context: TContext) => Promise<ToolOutcome>;
}

/**
 * What a tool hands back.
 *
 * <p>`content` is the only part the model sees. Everything else is for the UI: the paths a tool
 * touched drive the change-review pane, and `truncated` is what stops a clipped result being
 * read as a complete one.</p>
 */
export interface ToolOutcome {
  content: string;
  isError?: boolean;
  /** Workspace paths this call created, modified or deleted, for the run's change set. */
  paths?: readonly string[];
  /** True when `content` was clipped to the tool's ceiling. */
  truncated?: boolean;
}

/** One model-requested call, normalised away from any provider's block format. */
export interface ToolCall {
  /** The provider's id for the call — how a result is matched back to it. */
  id: string;
  name: string;
  input: Record<string, unknown>;
}

/**
 * What a person decided when the agent asked.
 *
 * <p>Three outcomes rather than two, because a yes/no card asked repeatedly teaches people to
 * click yes without reading — and the click that matters is the one on a card that says
 * <i>publish</i>. "For the rest of this run" lets somebody who has read one card say so once,
 * instead of being asked eight more times and stopping reading by the third.</p>
 *
 * <p>It grants a tool <b>name</b>, for <b>this run</b>. It is deliberately not a setting: a
 * standing permission nobody remembers granting is the thing this whole model exists to avoid.</p>
 */
export type ApprovalDecision = 'once' | 'run' | 'deny';

/** A call joined to what happened when it ran. */
export interface ToolResult extends ToolOutcome {
  callId: string;
  name: string;
  startedAt: number;
  endedAt: number;
}

// ---------------------------------------------------------------------------
// Events
// ---------------------------------------------------------------------------

/**
 * What a run emits as it happens.
 *
 * <p>The point is perceived speed. A run that shows nothing for four seconds and then everything
 * at once feels slower than an identical one that says "searching", "reading Hero.tsx",
 * "running checks" on the way — even when the totals match exactly. So the runtime streams
 * <i>agent</i> events, not just model tokens, and the panel renders progress rather than a
 * spinner.</p>
 *
 * <p>Discriminated on `type` so a surface can handle the events it cares about and ignore the
 * rest without a cast.</p>
 */
export type AgentEvent =
  | { type: 'agent.started'; runId: string; task: string }
  | { type: 'agent.thinking'; delta: string }
  | { type: 'text.delta'; delta: string }
  /**
   * One model turn finished. Carries what it cost, because per-run totals are the unit the
   * rework is judged in and the runtime is the only place that sees every turn.
   */
  | { type: 'turn.completed'; turn: number; usage: TurnUsage | null; model: string | null }
  | { type: 'tool.started'; call: ToolCall; label: string; risk: ToolRisk | 'read' }
  | { type: 'tool.completed'; result: ToolResult }
  | { type: 'tool.declined'; callId: string }
  /** A workspace path changed. Carries no content: the VFS already has it. */
  | { type: 'file.changed'; path: string; change: 'created' | 'modified' | 'deleted' }
  | { type: 'validation.started'; scope: string }
  | { type: 'validation.passed'; scope: string }
  | { type: 'validation.failed'; scope: string; problems: number }
  | { type: 'agent.retrying'; attempt: number; of: number; because: string }
  | { type: 'agent.completed'; runId: string }
  | { type: 'agent.failed'; runId: string; message: string }
  | { type: 'agent.stopped'; runId: string };

// ---------------------------------------------------------------------------
// Workspace
// ---------------------------------------------------------------------------

/**
 * Where a run's view of the workspace was pinned.
 *
 * <p>Every tool call operates against one of these. It is what makes a patch refusable: the
 * agent edits at the revision it read, and if the human typed into the same region meanwhile
 * the hash no longer matches and the edit is refused rather than silently overwriting them.</p>
 */
export interface WorkspaceRevision {
  siteId: string;
  branch: string;
  /** Monotonic per load; the VFS's own `rev`. Local, not the server's draft version. */
  revision: number;
  /** Path → content hash, as of this revision. */
  hashes: Readonly<Record<string, string>>;
}

/** One file, or a slice of one, as a tool returns it. */
export interface FileSlice {
  path: string;
  content: string;
  hash: string;
  /** 1-based and inclusive. Absent when the whole file was returned. */
  range?: { start: number; end: number };
  /** Total lines in the file, so the model can tell a slice from the whole. */
  totalLines: number;
}

/**
 * A read the workspace declined to repeat.
 *
 * <p>The answer to the loop that eats an agent's budget: read a file, edit it, read the whole
 * thing back to check, edit again. When the caller already holds the current hash there is
 * nothing to send, and saying so costs a dozen tokens instead of a thousand.</p>
 */
export interface UnchangedFile {
  path: string;
  hash: string;
  unchanged: true;
}

/** One search hit, deliberately small: a place to look, not the thing itself. */
export interface SearchHit {
  path: string;
  /** 1-based. */
  line: number;
  /** The matching line, clipped. Enough to judge relevance without opening the file. */
  preview: string;
}

// ---------------------------------------------------------------------------
// Context budget
// ---------------------------------------------------------------------------

/**
 * The ceiling a run reasons inside.
 *
 * <p>Without one the agent's strategy is "read everything", which is correct exactly until the
 * project stops being small. With one, the context manager has to choose — read this file, or
 * summarise what it already has — and choosing is the behaviour the whole rework is trying to
 * buy.</p>
 */
export interface ContextBudget {
  /** Ceiling on the assembled request, before the model is called. */
  maxInputTokens: number;
  /** Ceiling on one tool result, before truncation. */
  maxToolResultTokens: number;
  /** How many whole files one turn may pull in before it must narrow to ranges. */
  maxFilesPerRead: number;
}

/**
 * Starting budgets, to be re-tuned against `benchmarks/ai-agent.json` rather than by feel.
 *
 * <p>These are the plan's Phase 6 acceptance targets expressed as limits: a simple edit inside
 * 10k input tokens, a normal feature inside 40k.</p>
 */
export const DEFAULT_CONTEXT_BUDGET: ContextBudget = {
  maxInputTokens: 40_000,
  maxToolResultTokens: 8_000,
  maxFilesPerRead: 5,
};

/**
 * Rough token count for a string.
 *
 * <p>Four characters per token is the usual English approximation and it is wrong for source
 * code — dense punctuation tokenises harder — so this <b>over</b>-estimates on purpose by using
 * 3.5. A budget that guesses low overruns silently; one that guesses high trims a little early,
 * which is the survivable direction. Exact accounting comes from the provider's own usage
 * numbers after the fact (`ide/agent/runMetrics.ts`); this is only for deciding what to send.</p>
 */
export function estimateTokens(text: string): number {
  return Math.ceil(text.length / 3.5);
}
