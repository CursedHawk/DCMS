/**
 * How much the assistant is allowed to do on its own.
 *
 * <p>The old switch was a boolean — read, or write-with-a-card-for-every-change — and the
 * second half was the problem. Asking permission to save a draft teaches an operator to click
 * Allow without reading, which is precisely the habit you do not want them to have when the
 * card in front of them says <i>publish</i>. So the modes below separate the two questions:
 * what the model is offered at all, and which of those stop for a person.</p>
 */
export type AiMode = 'read' | 'careful' | 'agent' | 'auto';

/**
 * How much damage a tool can do, not whether it writes.
 *
 * <p><b>safe</b> — drafting, editing, uploading, filing. Everything it does stays inside the
 * workspace and a person can undo it by hand.</p>
 * <p><b>dangerous</b> — publishing, scheduling a publish, deleting. Either the public sees the
 * result or the thing is gone; neither is undone by editing a draft.</p>
 */
export type ToolRisk = 'safe' | 'dangerous';

/** What happens when the model asks for a tool of a given risk in a given mode. */
export type Decision = 'run' | 'approve' | 'unavailable';

export const AI_MODES: readonly AiMode[] = ['read', 'careful', 'agent', 'auto'];

/** The default. Writes without ceremony; stops at the two things that are hard to take back. */
export const DEFAULT_MODE: AiMode = 'agent';

/**
 * The whole safety model, in one table.
 *
 * <p>Read tools are never gated: a tool the caller's permissions do not reach is not offered in
 * the first place (see `toolsFor`), so anything the model can read here it could read by
 * clicking around the console.</p>
 */
export function decide(mode: AiMode, risk: ToolRisk | 'read'): Decision {
  if (risk === 'read') return 'run';
  switch (mode) {
    case 'read':
      return 'unavailable';
    case 'careful':
      return 'approve';
    case 'agent':
      return risk === 'dangerous' ? 'approve' : 'run';
    case 'auto':
      return 'run';
  }
}

/** True when this mode offers the model any writing tool at all. */
export function writesIn(mode: AiMode): boolean {
  return mode !== 'read';
}

const MODE_KEY = 'dcms.ai.mode';

/**
 * The mode this browser starts in.
 *
 * <p><b>Full auto is deliberately not remembered.</b> Every other mode is a preference; that
 * one is a decision to let publishing and deleting happen with nobody watching, and a decision
 * like that should be made by the person sitting there, not restored from a fortnight ago by a
 * browser that still had the key. It falls back to Agent, which still writes freely.</p>
 */
export function storedMode(): AiMode {
  try {
    const saved = localStorage.getItem(MODE_KEY);
    return saved === 'read' || saved === 'careful' || saved === 'agent' ? saved : DEFAULT_MODE;
  } catch {
    // A browser refusing storage is not a reason to refuse the assistant.
    return DEFAULT_MODE;
  }
}

export function rememberMode(mode: AiMode): void {
  try {
    // `auto` is stored as `agent`: see storedMode.
    localStorage.setItem(MODE_KEY, mode === 'auto' ? DEFAULT_MODE : mode);
  } catch {
    // Ignored, as above.
  }
}
