/**
 * `ask_user`: the agent asks the person a few questions before it builds the wrong thing.
 *
 * <p>Shared by both surfaces — site agents (`host.ts`) and the console assistant
 * (`useAssistantSession.ts`) — which differ only in where the questions wait: the host store for a
 * site agent, the assistant's own state for the assistant. Both render them with `QuestionCard`.</p>
 *
 * <p>A read-risk tool: asking changes nothing, so no mode gates it. The run simply waits until the
 * person answers, skips, or stops it.</p>
 */

export interface AgentQuestion {
  question: string;
  /** A few words naming the topic, for the card's chip. */
  header?: string;
  options: { label: string; description?: string }[];
  /** True when more than one option may be chosen. */
  multiSelect?: boolean;
}

/** One answer per question: the chosen option labels, and what the person wrote themselves. */
export interface AgentAnswer {
  selected: string[];
  other?: string;
}

/** What a person did with a set of questions: answered them, or chose not to (`null`). */
export type AgentAnswers = AgentAnswer[] | null;

export const ASK_USER_NAME = 'ask_user';

export const ASK_USER = {
  name: ASK_USER_NAME,
  description:
    'Ask the user one to four questions and wait for their answers. Use it when the request is ambiguous and the answer changes what you would build — a choice of layout, scope, wording or behaviour — ideally before you start, not halfway through. Do not ask what you can find out with your other tools or decide sensibly yourself. Put every question you have into ONE call. Each question offers 2–6 concrete options; the user can always write their own answer instead. Recommend an option by putting it first and ending its label with "(recommended)".',
  input_schema: {
    type: 'object',
    properties: {
      questions: {
        type: 'array',
        minItems: 1,
        maxItems: 4,
        items: {
          type: 'object',
          properties: {
            question: { type: 'string', description: 'The whole question, ending with a question mark.' },
            header: { type: 'string', description: 'One to three words naming the topic, e.g. "Layout".' },
            options: {
              type: 'array',
              minItems: 2,
              maxItems: 6,
              items: {
                type: 'object',
                properties: {
                  label: { type: 'string', description: 'The choice, in a few words.' },
                  description: { type: 'string', description: 'What choosing it means, in one sentence.' },
                },
                required: ['label'],
                additionalProperties: false,
              },
            },
            multiSelect: { type: 'boolean', description: 'True when several options may be chosen together.' },
          },
          required: ['question', 'options'],
          additionalProperties: false,
        },
      },
    },
    required: ['questions'],
    additionalProperties: false,
  } as Record<string, unknown>,
} as const;

/** The model's input as questions, or an error message it can correct. */
export function parseQuestions(input: Record<string, unknown>): AgentQuestion[] | string {
  const raw = input.questions;
  if (!Array.isArray(raw) || raw.length === 0) return 'ask_user needs a non-empty "questions" array.';
  if (raw.length > 4) return 'Ask at most four questions at a time.';
  const questions: AgentQuestion[] = [];
  for (const q of raw as Record<string, unknown>[]) {
    const question = typeof q?.question === 'string' ? q.question.trim() : '';
    const options = Array.isArray(q?.options)
      ? (q.options as Record<string, unknown>[])
          .map((o) => ({
            label: typeof o?.label === 'string' ? o.label.trim() : '',
            description: typeof o?.description === 'string' ? o.description : undefined,
          }))
          .filter((o) => o.label)
      : [];
    if (!question) return 'Every question needs its "question" text.';
    if (options.length < 2) return `"${question}" needs at least two options.`;
    questions.push({
      question,
      header: typeof q.header === 'string' && q.header.trim() ? q.header.trim() : undefined,
      options: options.slice(0, 6),
      multiSelect: q.multiSelect === true,
    });
  }
  return questions;
}

/** The answers as the model reads them. */
export function formatAnswers(questions: readonly AgentQuestion[], answers: AgentAnswers): string {
  if (answers === null) {
    return 'The user skipped these questions. Use your best judgement, and say in your reply what you assumed.';
  }
  const lines = questions.map((q, index) => {
    const answer = answers[index] ?? { selected: [] };
    const parts = [...answer.selected];
    if (answer.other?.trim()) parts.push(`their own answer: "${answer.other.trim()}"`);
    return `${index + 1}. ${q.question}\n   → ${parts.length > 0 ? parts.join('; ') : '(no answer)'}`;
  });
  return `The user answered:\n${lines.join('\n')}`;
}

/** One line for the transcript card. */
export function describeAsk(input: Record<string, unknown>): string {
  const count = Array.isArray(input.questions) ? input.questions.length : 0;
  return count === 1 ? 'Asked you a question' : `Asked you ${count} questions`;
}
