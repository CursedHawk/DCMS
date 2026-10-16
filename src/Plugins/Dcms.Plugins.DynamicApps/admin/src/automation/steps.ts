import type { FlowActionInfo, FlowStep } from '../api';

/**
 * A flow step as its card edits it. The input is JSON text, so a half-typed object is kept rather
 * than lost; a script step's code is held apart from the rest of its input, so it is edited as
 * TypeScript instead of as one long JSON string. `toStep` puts the step back together.
 */
export interface StepDraft {
  /** Stable for the card's life: names its editors' documents, so they survive a reorder. */
  uid: string;
  id: string;
  action: string;
  condition: string;
  inputText: string;
  /** The script's source for script.run@1; null for every other action. */
  code: string | null;
}

export const SCRIPT_ACTION = 'script.run@1';

export const SCRIPT_TEMPLATE = `export default function main(ctx: FlowContext) {
  return {};
}
`;

let counter = 0;

export function toDraft(step: FlowStep): StepDraft {
  const script = step.action === SCRIPT_ACTION;
  const { code, ...rest } = step.input as Record<string, unknown> & { code?: unknown };
  return {
    uid: `step-${++counter}`,
    id: step.id,
    action: step.action,
    condition: step.condition ?? '',
    inputText: JSON.stringify(script ? rest : step.input, null, 2),
    code: script ? (typeof code === 'string' ? code : SCRIPT_TEMPLATE) : null,
  };
}

/** The input object, or null while the text is not one. */
export function parseInput(text: string): Record<string, unknown> | null {
  try {
    const value: unknown = JSON.parse(text.trim() === '' ? '{}' : text);
    return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/** The step as a flow stores it, or null while its input is not valid JSON. */
export function toStep(draft: StepDraft): FlowStep | null {
  const input = parseInput(draft.inputText);
  if (!input) return null;
  const step: FlowStep = { id: draft.id, action: draft.action, input: draft.code === null ? input : { ...input, code: draft.code } };
  if (draft.condition.trim() !== '') step.condition = draft.condition;
  return step;
}

/** A new action for the step: becoming a script brings a starting function, leaving one drops it. */
export function withAction(draft: StepDraft, action: string): StepDraft {
  if (action === SCRIPT_ACTION && draft.code === null) {
    return { ...draft, action, code: SCRIPT_TEMPLATE, inputText: JSON.stringify({ language: 'ts', args: {} }, null, 2) };
  }
  if (action !== SCRIPT_ACTION && draft.code !== null) {
    return { ...draft, action, code: null, inputText: '{}' };
  }
  return { ...draft, action };
}

/** `ts` or `js`, as the script's input says. */
export function scriptLanguage(draft: StepDraft): 'ts' | 'js' {
  return parseInput(draft.inputText)?.language === 'js' ? 'js' : 'ts';
}

/** The schema the JSON input editor checks against: a script's code is edited apart, so not here. */
export function inputSchemaFor(action: FlowActionInfo | undefined, script: boolean): Record<string, unknown> | undefined {
  const schema = action?.inputSchema;
  if (!schema || !script) return schema;
  const { code: _code, ...properties } = (schema.properties ?? {}) as Record<string, unknown>;
  const required = Array.isArray(schema.required) ? schema.required.filter((r) => r !== 'code') : undefined;
  return { ...schema, properties, required };
}

/** An id not yet used in the flow: step_1, step_2, … */
export function freeStepId(drafts: readonly StepDraft[]): string {
  let n = drafts.length + 1;
  while (drafts.some((d) => d.id === `step_${n}`)) n++;
  return `step_${n}`;
}
