import { describe, expect, it } from 'vitest';
import type { ContentBlock, Message } from '../ide/agent/client';
import { estimateMessages, findOrphanedToolUses } from './context';

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

describe('estimateMessages', () => {
  it('counts text, tool input and tool results', () => {
    expect(estimateMessages([user('hello')])).toBeGreaterThan(0);
    expect(estimateMessages([toolResult('1', big(3500))])).toBeGreaterThan(900);
  });

  it('is zero for an empty conversation', () => {
    expect(estimateMessages([])).toBe(0);
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
