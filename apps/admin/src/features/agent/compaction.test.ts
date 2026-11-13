import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../../lib/api';
import type { Message } from '../ide/agent/client';
import {
  archive,
  COMPACTION_HEADER,
  contextOf,
  continuation,
  conversationFolder,
  parseSummary,
  promptTokens,
  summarize,
} from './compaction';
import { findOrphanedToolUses } from './context';
import { withCacheHints } from './modelRouter';

vi.mock('../../lib/api', () => ({
  api: { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), del: vi.fn(), upload: vi.fn() },
}));

/** A conversation with thinking, a tool round and its result: what a real run leaves behind. */
function conversation(): Message[] {
  return [
    { role: 'user', content: 'Build the HR portal' },
    {
      role: 'assistant',
      content: [
        { type: 'thinking', thinking: 'plan', signature: 'sig-1' },
        { type: 'tool_use', id: 't1', name: 'read_file', input: { path: 'src/App.tsx' } },
      ] as never,
    },
    { role: 'user', content: [{ type: 'tool_result', tool_use_id: 't1', content: 'export default …' }] as never },
  ];
}

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.put).mockImplementation(async (_p: string, body: unknown) => ({ ...(body as object), id: 'k' }) as never);
});

describe('when to compact', () => {
  it("counts the provider's own numbers for the last request, and estimates only what came after", () => {
    const tokens = promptTokens({
      lastUsage: { inputTokens: 1_000, cacheReadTokens: 80_000, cacheCreationTokens: 5_000, outputTokens: 2_000 },
      appended: [{ role: 'user', content: 'x'.repeat(3_500) }],
      context: [],
      system: '',
      tools: [],
    });
    expect(tokens).toBe(1_000 + 80_000 + 5_000 + 2_000 + 1_000);
  });

  it('estimates the whole request when the provider reports nothing', () => {
    const tokens = promptTokens({
      lastUsage: { inputTokens: null, cacheReadTokens: 0, cacheCreationTokens: 0, outputTokens: 0 },
      appended: [],
      context: [{ role: 'user', content: 'x'.repeat(350) }],
      system: 'y'.repeat(35),
      tools: [],
    });
    expect(tokens).toBe(100 + 10 + 1);
  });
});

describe('the summary request', () => {
  it('is the previous request with one message appended, so the whole prefix is a cache read', async () => {
    const history = conversation();
    const system = 'You are the agent.';
    const tools = [{ name: 'read_file', description: 'r', input_schema: { type: 'object' } }];
    const params = { thinking: { type: 'adaptive' }, output_config: { effort: 'medium' }, max_tokens: 12_000 };
    const previous = withCacheHints({ ...params, system, tools, messages: contextOf(history) });

    const transport = vi.fn(async () => ({
      content: [{ type: 'text', text: '<summary>Built half.</summary><knowledge>[]</knowledge>' }],
      stopReason: 'end_turn',
      usage: null,
      model: null,
    }));
    await summarize({ system, tools, context: contextOf(history), params, transport: transport as never, signal: new AbortController().signal });

    const sent = (transport.mock.calls[0] as unknown[])[0] as Record<string, unknown>;
    const messages = sent.messages as Message[];
    expect(JSON.stringify(messages.slice(0, -1))).toBe(JSON.stringify(previous.messages));
    expect(sent.system).toEqual(previous.system);
    expect(sent.tools).toEqual(previous.tools);
    expect(sent.thinking).toEqual(params.thinking);
    expect(sent.output_config).toEqual(params.output_config);
    expect(JSON.stringify(messages.at(-1))).toMatch(/Do not call any tools/);
  });

  it('reads the summary and the lasting facts, and tolerates a model that ignored the tags', () => {
    const parsed = parseSummary(
      '<summary>We chose cards.</summary>\n<knowledge>[{"path":"Decisions/Hero Layout","title":"Hero","description":"Cards","body":"Because."},{"path":"loose","title":"x","description":"x","body":"x"},{"bad":1}]</knowledge>',
    );
    expect(parsed.summary).toBe('We chose cards.');
    // Only well-formed items, inside a folder, under a clean path.
    expect(parsed.knowledge).toEqual([{ path: 'decisions/hero-layout', title: 'Hero', description: 'Cards', body: 'Because.' }]);

    expect(parseSummary('Just prose.').summary).toBe('Just prose.');
    expect(parseSummary('<summary>S</summary><knowledge>not json</knowledge>').knowledge).toEqual([]);
  });
});

describe('after compaction', () => {
  it('sends only the continuation: no earlier turn, no thinking, no orphaned tool call — then appends', () => {
    const transcript = [...conversation(), continuation({ summary: 'Built half.', archivedAt: null, saved: [] })];
    transcript.push({ role: 'assistant', content: [{ type: 'text', text: 'Carrying on.' }] as never });

    const first = contextOf(transcript);
    expect(first).toHaveLength(2);
    expect(JSON.stringify(first[0])).toContain(COMPACTION_HEADER);
    expect(JSON.stringify(first)).not.toContain('thinking');
    expect(findOrphanedToolUses(first)).toEqual([]);
    // Stored kind never reaches the wire.
    expect(first[0]).not.toHaveProperty('kind');

    transcript.push({ role: 'user', content: 'Next' });
    const second = contextOf(transcript);
    expect(JSON.stringify(second.slice(0, first.length))).toBe(JSON.stringify(first));
  });

  it('points at the archive and what was saved', () => {
    const text = JSON.stringify(
      continuation({ summary: 'S', archivedAt: 'conversations/2026-11-13-hr-1a2b3c4d/part-1', saved: ['decisions/hero'] }),
    );
    expect(text).toContain('conversation_recall');
    expect(text).toContain('conversations/2026-11-13-hr-1a2b3c4d/part-1');
    expect(text).toContain('decisions/hero');
  });
});

describe('the archive', () => {
  it('is saved under the conversation’s folder with the range it covers, and its facts beside it', async () => {
    const result = await archive({
      siteId: 'site-a',
      conversationId: '1a2b3c4d-0000-0000-0000-000000000000',
      title: 'Create HR portal web page',
      part: 2,
      fromSeq: 1,
      toSeq: 58,
      summary: 'S',
      knowledge: [{ path: 'decisions/hero', title: 'Hero', description: 'Cards', body: 'B' }],
      mayWrite: true,
    });

    const [archiveCall, factCall] = vi.mocked(api.put).mock.calls.map((c) => c[1] as Record<string, unknown>);
    expect(archiveCall).toMatchObject({
      siteId: 'site-a',
      source: 'compaction',
      fromSeq: 1,
      toSeq: 58,
      conversationId: '1a2b3c4d-0000-0000-0000-000000000000',
    });
    expect(String(archiveCall.path)).toMatch(/^conversations\/\d{4}-\d{2}-\d{2}-create-hr-portal-web-page-1a2b3c4d\/part-2$/);
    expect(factCall).toMatchObject({ path: 'decisions/hero', source: 'compaction' });
    expect(result.saved).toEqual(['decisions/hero']);
  });

  it('is not attempted for a member who may not write knowledge', async () => {
    const result = await archive({
      siteId: null, conversationId: 'c', title: 't', part: 1, fromSeq: null, toSeq: null, summary: 'S', knowledge: [], mayWrite: false,
    });
    expect(api.put).not.toHaveBeenCalled();
    expect(result).toEqual({ archivedAt: null, saved: [] });
  });

  it('names its folder by date, title and conversation', () => {
    expect(conversationFolder('Řešení / Hero!', 'abcdef12-3456', new Date('2026-11-13T10:00:00Z'))).toBe('2026-11-13-reseni-hero-abcdef12');
  });
});
