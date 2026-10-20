import { describe, expect, it } from 'vitest';
import type { ContentBlock, Message } from '../ide/agent/client';
import type { ContextBudget } from './contracts';
import {
  estimateMessages,
  findOrphanedToolUses,
  newPruneState,
  pruneHistory,
  type WorkingMemory,
} from './context';

const BUDGET: ContextBudget = {
  maxInputTokens: 1000,
  maxToolResultTokens: 500,
  maxFilesPerRead: 5,
};

const user = (text: string): Message => ({ role: 'user', content: text });
const assistant = (text: string): Message => ({
  role: 'assistant',
  content: [{ type: 'text', text }] as ContentBlock[],
});
const usesTool = (id: string): Message => ({
  role: 'assistant',
  content: [{ type: 'tool_use', id, name: 'read_file', input: {} }] as ContentBlock[],
});
const toolResult = (id: string, content: string): Message => ({
  role: 'user',
  content: [{ type: 'tool_result', tool_use_id: id, content }] as unknown as ContentBlock[],
});

const big = (n: number) => 'x'.repeat(n);

const MEMORY: WorkingMemory = {
  goal: 'add an About page',
  filesChanged: ['src/About.tsx'],
  notes: ['The build failed once.'],
};

describe('estimateMessages', () => {
  it('counts text, tool input and tool results', () => {
    expect(estimateMessages([user('hello')])).toBeGreaterThan(0);
    expect(estimateMessages([toolResult('1', big(3500))])).toBeGreaterThan(900);
  });

  it('is zero for an empty conversation', () => {
    expect(estimateMessages([])).toBe(0);
  });
});

describe('pruneHistory', () => {
  it('leaves a small conversation untouched', () => {
    const messages = [user('hi'), assistant('hello')];
    const result = pruneHistory(messages, BUDGET);
    expect(result).toMatchObject({ elided: 0, dropped: 0 });
    expect(result.messages).toEqual(messages);
  });

  it("does not mutate the caller's history", () => {
    const original = [user('hi'), usesTool('1'), toolResult('1', big(8000)), assistant('done')];
    const snapshot = JSON.stringify(original);
    pruneHistory(original, BUDGET, MEMORY);
    expect(JSON.stringify(original)).toBe(snapshot);
  });

  it('elides old tool results before dropping anything', () => {
    const messages = [
      user('go'),
      usesTool('1'),
      toolResult('1', big(8000)),
      assistant('a'),
      user('b'),
      assistant('c'),
      user('d'),
      assistant('e'),
    ];
    const result = pruneHistory(messages, BUDGET, MEMORY);
    expect(result.elided).toBeGreaterThan(0);
    expect(result.estimated).toBeLessThanOrEqual(BUDGET.maxInputTokens);
  });

  it('says how to recover an elided result rather than silently blanking it', () => {
    const messages = [
      user('go'),
      usesTool('1'),
      toolResult('1', big(9000)),
      assistant('a'),
      user('b'),
      assistant('c'),
      user('d'),
      assistant('e'),
    ];
    const text = JSON.stringify(pruneHistory(messages, BUDGET, MEMORY).messages);
    expect(text).toContain('re-run the tool');
  });

  it('keeps the most recent exchanges verbatim', () => {
    const messages = [
      user('old'),
      usesTool('1'),
      toolResult('1', big(9000)),
      assistant('old reply'),
      user('recent question'),
      assistant('recent answer'),
    ];
    const result = pruneHistory(messages, BUDGET, MEMORY);
    const text = JSON.stringify(result.messages);
    // Losing the result the run is currently reasoning about is the one thing compression
    // must never do.
    expect(text).toContain('recent question');
    expect(text).toContain('recent answer');
  });

  it('drops whole exchanges when eliding is not enough', () => {
    const messages: Message[] = [];
    for (let i = 0; i < 12; i++) {
      messages.push(user(`question ${i} ${big(900)}`));
      messages.push(assistant(`answer ${i}`));
    }
    const result = pruneHistory(messages, BUDGET, MEMORY);
    expect(result.dropped).toBeGreaterThan(0);
    expect(result.estimated).toBeLessThanOrEqual(BUDGET.maxInputTokens * 1.5);
  });

  it('never orphans a tool_use when dropping', () => {
    // The constraint that shapes the whole compressor: an unanswered tool_use is a request the
    // provider refuses outright, and the failure surfaces far from its cause.
    const messages: Message[] = [];
    for (let i = 0; i < 10; i++) {
      messages.push(user(`ask ${i} ${big(600)}`));
      messages.push(usesTool(`t${i}`));
      messages.push(toolResult(`t${i}`, `result ${i} ${big(600)}`));
    }
    const result = pruneHistory(messages, BUDGET, MEMORY);
    expect(findOrphanedToolUses(result.messages)).toEqual([]);
  });

  it('carries the goal forward when history was compressed', () => {
    const messages: Message[] = [];
    for (let i = 0; i < 12; i++) {
      messages.push(user(`q ${i} ${big(900)}`));
      messages.push(assistant(`a ${i}`));
    }
    const text = JSON.stringify(pruneHistory(messages, BUDGET, MEMORY).messages);
    expect(text).toContain('add an About page');
    expect(text).toContain('src/About.tsx');
  });

  it('adds no memory block when nothing was compressed', () => {
    const text = JSON.stringify(pruneHistory([user('hi')], BUDGET, MEMORY).messages);
    expect(text).not.toContain('compressed');
  });

  it('does not elide the same result twice on repeated runs', () => {
    const messages = [
      user('go'),
      usesTool('1'),
      toolResult('1', big(9000)),
      assistant('a'),
      user('b'),
      assistant('c'),
      user('d'),
      assistant('e'),
    ];
    const once = pruneHistory(messages, BUDGET, MEMORY);
    const twice = pruneHistory(once.messages, BUDGET, MEMORY);
    expect(twice.elided).toBe(0);
  });
});

describe('pruneHistory across turns', () => {
  const thinks = (text: string, id: string): Message => ({
    role: 'assistant',
    content: [
      { type: 'thinking', thinking: 'hmm', signature: 'sig' },
      { type: 'text', text },
      { type: 'tool_use', id, name: 'read_file', input: {} },
    ] as ContentBlock[],
  });
  const longRun = (): Message[] => {
    const messages: Message[] = [user('go')];
    for (let i = 0; i < 8; i++) {
      messages.push(thinks(`step ${i}`, `t${i}`));
      messages.push(toolResult(`t${i}`, big(600)));
    }
    return messages;
  };

  it('compacts well under the budget, so the next turns have room to append', () => {
    const result = pruneHistory(longRun(), BUDGET, MEMORY, newPruneState());
    expect(result.estimated).toBeLessThanOrEqual(BUDGET.maxInputTokens * 0.6);
  });

  it('keeps the sent prefix byte-identical between compactions', () => {
    // A prefix that changes every turn misses the prompt cache every turn.
    const messages = longRun();
    const state = newPruneState();
    const first = pruneHistory(messages, BUDGET, MEMORY, state).messages;

    messages.push(thinks('next', 'n1'), toolResult('n1', 'small'));
    const second = pruneHistory(messages, BUDGET, { ...MEMORY, notes: ['changed'] }, state).messages;

    expect(JSON.stringify(second.slice(0, first.length))).toBe(JSON.stringify(first));
  });

  it('strips thinking from the history it rewrote, and only from that', () => {
    // A thinking block is bound to the history it was produced in; replaying it after that
    // history changed is a 400 on Opus 5.5 / Sonnet 5.5.
    const messages = longRun();
    const state = newPruneState();
    const compacted = pruneHistory(messages, BUDGET, MEMORY, state).messages;
    expect(JSON.stringify(compacted)).not.toContain('"thinking"');

    messages.push(thinks('next', 'n1'), toolResult('n1', 'small'));
    const next = pruneHistory(messages, BUDGET, MEMORY, state).messages;
    expect(next.filter((m) => JSON.stringify(m).includes('"thinking"'))).toHaveLength(1);
    expect(findOrphanedToolUses(next)).toEqual([]);
  });

  it('never edits an assistant turn except to strip its thinking', () => {
    const messages = longRun();
    const sent = pruneHistory(messages, BUDGET, MEMORY, newPruneState()).messages;
    for (const m of sent.filter((x) => x.role === 'assistant')) {
      const original = messages.find(
        (o) => o.role === 'assistant' && JSON.stringify(o).includes(JSON.stringify((m.content as ContentBlock[])[0])),
      );
      const withoutThinking = (original!.content as ContentBlock[]).filter((b) => b.type !== 'thinking');
      expect(m.content).toEqual(withoutThinking);
    }
  });
});

describe('findOrphanedToolUses', () => {
  it('finds an unanswered tool_use', () => {
    expect(findOrphanedToolUses([usesTool('a'), assistant('no result')])).toEqual(['a']);
  });

  it('accepts a properly answered one', () => {
    expect(findOrphanedToolUses([usesTool('a'), toolResult('a', 'ok')])).toEqual([]);
  });

  it('notices a mismatched id', () => {
    expect(findOrphanedToolUses([usesTool('a'), toolResult('b', 'ok')])).toEqual(['a']);
  });
});
