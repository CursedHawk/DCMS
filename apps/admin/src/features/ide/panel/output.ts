/**
 * What the IDE itself did, as a log.
 *
 * <p>VS Code's Output panel carries what tools and extensions say. The equivalent here is
 * everything the workspace does on the author's behalf without being asked: autosaves, branch
 * switches, agent runs, publishes, sandbox resets. Until now those were toasts — which are the
 * right shape for "this just happened" and the wrong shape for "what happened while I was
 * reading", because a toast that has faded is gone.</p>
 *
 * <p><b>Module state rather than a store.</b> Callers are hooks, mutation callbacks, the agent
 * runtime and the draft session — none of which share a React tree branch, and several of which
 * run outside a component entirely. A subscribable module is what all of them can reach.</p>
 */

export type OutputChannel = 'workspace' | 'agent' | 'git' | 'preview';

export type OutputLevel = 'info' | 'warn' | 'error';

export interface OutputLine {
  id: number;
  channel: OutputChannel;
  level: OutputLevel;
  text: string;
  at: number;
}

/**
 * Ring buffer size.
 *
 * <p>An editing session left open all day produces a steady trickle; a save loop or a failing
 * build produces a flood. Keeping the newest 500 bounds memory without truncating anything an
 * author is realistically scrolling back through.</p>
 */
const MAX_LINES = 500;

let lines: OutputLine[] = [];
let seq = 0;
const listeners = new Set<() => void>();

function emit(): void {
  for (const listener of listeners) listener();
}

export function appendOutput(
  channel: OutputChannel,
  text: string,
  level: OutputLevel = 'info',
): void {
  const trimmed = text.trim();
  if (!trimmed) return;

  /*
   * Collapse an immediate repeat into a count rather than a new line.
   *
   * A rebuild that fails the same way on every keystroke would otherwise push forty identical
   * lines and scroll the thing that actually changed off the top. Only the newest line is
   * considered, so an alternating pair still reads as two.
   */
  const last = lines[lines.length - 1];
  if (last && last.channel === channel && last.level === level && baseText(last.text) === trimmed) {
    const count = repeatCount(last.text) + 1;
    lines = [...lines.slice(0, -1), { ...last, text: `${trimmed}  (×${count})`, at: Date.now() }];
    emit();
    return;
  }

  lines = [...lines, { id: ++seq, channel, level, text: trimmed, at: Date.now() }];
  if (lines.length > MAX_LINES) lines = lines.slice(-MAX_LINES);
  emit();
}

const REPEAT = /\s{2}\(×(\d+)\)$/;

function baseText(text: string): string {
  return text.replace(REPEAT, '');
}

function repeatCount(text: string): number {
  const match = REPEAT.exec(text);
  return match ? Number(match[1]) : 1;
}

export function outputLines(): readonly OutputLine[] {
  return lines;
}

export function clearOutput(): void {
  lines = [];
  emit();
}

export function subscribeOutput(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** Test seam. Nothing in the app calls this. */
export function resetOutput(): void {
  lines = [];
  seq = 0;
  listeners.clear();
}
