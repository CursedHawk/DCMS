/**
 * How much ceremony a task deserves, decided before any model is called.
 *
 * <p>The failure this prevents is the agent that answers "change the button text to Buy now"
 * with "I'll first formulate a comprehensive implementation plan…". That is a wasted turn and
 * several seconds of latency on a task that needed one edit, and it is most of why some coding
 * agents feel slow on exactly the work that should feel instant.</p>
 *
 * <h3>Why a heuristic and not a model call</h3>
 * <p>Asking a cheap model to classify costs a round trip — which on a trivial task is most of
 * the time the whole task should have taken. A classifier that costs what it saves is not a
 * saving. So this reads the request text and the workspace, both free, and is allowed to be
 * wrong: the cost of under-classifying is that the agent plans a little when it needn't, and of
 * over-classifying is that it dives in and does more turns than a plan would have taken. Neither
 * is a correctness problem, which is what makes a heuristic acceptable here at all.</p>
 */

export type Complexity = 'trivial' | 'normal' | 'complex';

export interface Classification {
  complexity: Complexity;
  /** Whether to spend a turn producing a plan before acting. */
  plan: boolean;
  /** Human-readable reason, shown in the panel so the choice is never mysterious. */
  because: string;
}

/** Verbs that describe changing one thing in place. */
const SMALL_VERBS =
  /\b(rename|change|fix|tweak|adjust|update|set|replace|swap|remove|delete|add)\b/i;

/** Words that reliably mean more than one file is in play. */
const BIG_WORDS =
  /\b(refactor|redesign|rewrite|restructure|migrate|architecture|architect|overhaul|throughout|across|every (page|component|file)|all (pages|components|files)|end.to.end)\b/i;

/** Words that mean the request is under-specified and guessing would be expensive. */
const VAGUE_WORDS = /\b(better|nicer|improve|modern|clean up|polish|make it (good|nice|pretty))\b/i;

/**
 * Things that are never one edit, however briefly they are asked for.
 *
 * <p>"add" is in the small-verb list because "add a class to the button" genuinely is one edit.
 * But "add an About page" is a file, a route and a nav entry — the verb is the same size and the
 * work is not. The noun is what separates them.</p>
 */
const STRUCTURAL_NOUNS =
  /\b(page|route|screen|view|component|section|feature|form|layout|nav|navigation|endpoint|api)\b/i;

export function classify(task: string, context: { fileCount?: number } = {}): Classification {
  const text = task.trim();
  const words = text.split(/\s+/).length;

  if (BIG_WORDS.test(text)) {
    return {
      complexity: 'complex',
      plan: true,
      because: 'the request names a change across many files',
    };
  }

  if (VAGUE_WORDS.test(text)) {
    // Vague but small-sounding is the trap: "make the hero nicer" is one file and a dozen
    // judgement calls, and diving in produces work the author did not ask for. A plan here is
    // a cheap way to agree on scope before spending output tokens on it.
    return {
      complexity: 'complex',
      plan: true,
      because: 'the request is open-ended, so the scope is worth agreeing first',
    };
  }

  // A long request is usually a list of requirements, whatever verbs it uses.
  if (words > 60) {
    return { complexity: 'complex', plan: true, because: 'the request is long and multi-part' };
  }

  if (words <= 20 && SMALL_VERBS.test(text) && !STRUCTURAL_NOUNS.test(text)) {
    return {
      complexity: 'trivial',
      plan: false,
      because: 'a short, specific change',
    };
  }

  if ((context.fileCount ?? 0) === 0) {
    // Nothing to reason about yet — a brand-new site. Whatever was asked, it starts by
    // scaffolding, and planning the scaffold is ceremony.
    return { complexity: 'normal', plan: false, because: 'an empty workspace' };
  }

  return { complexity: 'normal', plan: false, because: 'an ordinary change' };
}
