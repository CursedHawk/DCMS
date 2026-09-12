import { describe, expect, it } from 'vitest';
import { capResult } from './runtime';
import type { ToolSpec } from './contracts';
import { AI_MODES, decide } from './modes';
import { ALL_TOOLS } from '../ide/agent/checkTools';
import { SANDBOX_TOOLS, SKILL_TOOLS, TENANT_TOOLS } from '../ide/agent/tenantTools';

/**
 * The rules a tool has to obey to be offered at all.
 *
 * <h3>Why these are tests and not a checklist</h3>
 * <p>Both of the properties asserted here — that dangerous things are marked dangerous, and that
 * no tool can return an unbounded result — hold across the registry <i>today</i>. They were
 * checked by hand once. That is worth exactly one afternoon: the next person adds
 * <c>delete_branch</c>, forgets <c>risk</c>, and it runs without asking in every mode including
 * Careful. A property nobody can break by accident is one a test is defending.</p>
 *
 * <p>Deliberately over the <b>real</b> registries rather than fixtures. A fixture would prove the
 * rule and not the code.</p>
 */

const REGISTRY: ToolSpec<unknown>[] = [
  ...(ALL_TOOLS as unknown as ToolSpec<unknown>[]),
  ...(SKILL_TOOLS as unknown as ToolSpec<unknown>[]),
  ...(SANDBOX_TOOLS as unknown as ToolSpec<unknown>[]),
  ...(TENANT_TOOLS as unknown as ToolSpec<unknown>[]),
];

/**
 * Verbs that make a tool `dangerous`, from the risk model in `modes.ts`.
 *
 * <p>Risk is about <b>damage, not writing</b>: either the public sees the result or the thing is
 * gone, and neither is undone by editing a draft. Everything else a tool does stays inside the
 * workspace where a person can put it back.</p>
 *
 * <p>`delete_lines` is the deliberate exception and it is not an oversight — it deletes text
 * inside a file the agent's own transaction can revert, which is the definition of `safe` here.
 * Matching on a whole leading word rather than a substring is what keeps it out.</p>
 */
const DANGEROUS_VERBS = ['publish', 'unpublish', 'schedule', 'delete', 'merge', 'discard'];

/** The workspace paths a dangerous-sounding verb is nonetheless safe on, with why. */
const SAFE_DESPITE_VERB: Record<string, string> = {
  delete_lines: 'edits text inside one file; the run transaction reverts it like any other edit',
};

function leadingVerb(name: string): string {
  return name.split('_')[0] ?? name;
}

describe('the tool registry', () => {
  it('offers every tool under a unique name', () => {
    // Two tools with one name is a coin toss at dispatch, and the loser is silently unreachable.
    const seen = new Map<string, number>();
    for (const tool of REGISTRY) seen.set(tool.name, (seen.get(tool.name) ?? 0) + 1);
    expect([...seen].filter(([, n]) => n > 1)).toEqual([]);
  });

  it('tells the model when not to call a tool, not only what it does', () => {
    // The description is the only documentation the model gets, and a vague one produces
    // expensive runs rather than broken ones — which is why they survive review.
    for (const tool of REGISTRY) {
      expect(tool.description.length, tool.name).toBeGreaterThan(40);
    }
  });

  it('accepts only the arguments it declares', () => {
    // Without this a model invents a plausible extra argument, the tool ignores it, and the
    // run proceeds believing it asked for something it did not.
    for (const tool of REGISTRY) {
      expect(tool.input_schema, tool.name).toMatchObject({ additionalProperties: false });
    }
  });
});

describe('risk (7.3)', () => {
  it('marks every publishing or destroying tool dangerous', () => {
    const wrong = REGISTRY.filter(
      (t) =>
        DANGEROUS_VERBS.includes(leadingVerb(t.name)) &&
        !(t.name in SAFE_DESPITE_VERB) &&
        t.risk !== 'dangerous',
    ).map((t) => t.name);

    expect(
      wrong,
      'a tool whose name says it publishes or destroys must be risk: "dangerous", or be listed ' +
        'in SAFE_DESPITE_VERB with the reason it is not',
    ).toEqual([]);
  });

  it('keeps the exception list honest', () => {
    // An entry for a tool that no longer exists silently exempts the next tool to take its name.
    const names = new Set(REGISTRY.map((t) => t.name));
    expect(Object.keys(SAFE_DESPITE_VERB).filter((n) => !names.has(n))).toEqual([]);
  });

  it('gives every gated tool both a card and a transcript line', () => {
    // A tool with a risk will be shown on an approval card, and "run publish_content?" with a
    // JSON blob under it is the shape people learn to click through without reading.
    for (const tool of REGISTRY.filter((t) => t.risk)) {
      expect(typeof tool.summarize, `${tool.name} needs summarize() for its approval card`).toBe(
        'function',
      );
      expect(typeof tool.describe, `${tool.name} needs describe() for the transcript`).toBe(
        'function',
      );
    }
  });

  it('never lets a dangerous tool run unattended outside full auto', () => {
    // The whole safety model, asserted against the registry rather than the table in isolation.
    for (const tool of REGISTRY.filter((t) => t.risk === 'dangerous')) {
      expect(decide('read', tool.risk!), tool.name).toBe('unavailable');
      expect(decide('careful', tool.risk!), tool.name).toBe('approve');
      expect(decide('agent', tool.risk!), tool.name).toBe('approve');
      expect(decide('auto', tool.risk!), tool.name).toBe('run');
    }
  });

  it('offers read-only tools in every mode, including read', () => {
    // Reading is never gated: a tool the caller's permissions do not reach is not offered at
    // all, so anything readable here was readable by clicking around the console.
    for (const tool of REGISTRY.filter((t) => !t.risk)) {
      for (const mode of AI_MODES) expect(decide(mode, 'read'), tool.name).toBe('run');
    }
  });
});

describe('result size discipline (5.9)', () => {
  it('bounds every tool, whether or not it sets its own ceiling', () => {
    // The runtime's default is the floor of this guarantee: a tool that names no cap still
    // cannot spend a run's whole budget on one directory listing.
    const huge = 'x'.repeat(100_000);
    for (const tool of REGISTRY) {
      const { content, truncated } = capResult(huge, tool.maxResultChars);
      expect(truncated, tool.name).toBe(true);
      expect(content.length, tool.name).toBeLessThan(huge.length);
    }
  });

  it('tells the model how to get the rest, every time it truncates', () => {
    // A clipped result that does not say it was clipped is read as a complete answer, and the
    // model reasons from a file it has only seen half of.
    const { content } = capResult('y'.repeat(50_000), 1000);
    expect(content).toContain('truncated');
    expect(content).toMatch(/narrow the request/i);
  });

  it('leaves a result that fits completely alone', () => {
    const { content, truncated } = capResult('small', 1000);
    expect(truncated).toBe(false);
    expect(content).toBe('small');
  });

  it('keeps explicit ceilings inside a band that means something', () => {
    // A cap of 200k is a cap in name only, and one of 200 truncates mid-sentence forever.
    for (const tool of REGISTRY.filter((t) => t.maxResultChars !== undefined)) {
      expect(tool.maxResultChars, tool.name).toBeGreaterThanOrEqual(1000);
      expect(tool.maxResultChars, tool.name).toBeLessThanOrEqual(32_000);
    }
  });
});
