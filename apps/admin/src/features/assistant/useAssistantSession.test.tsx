import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PermissionProvider } from '@dcms/ui';
import { streamAssistantTurn } from '../ide/agent/client';
import { api } from '../../lib/api';
import type { ToolStep } from './steps';
import { useAssistantSession } from './useAssistantSession';

vi.mock('../ide/agent/client', async (original) => ({
  ...(await original<typeof import('../ide/agent/client')>()),
  streamAssistantTurn: vi.fn(),
}));
vi.mock('../../lib/api', () => ({
  api: { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), del: vi.fn(), upload: vi.fn() },
}));

/**
 * The approval gate, which is the only thing standing between a misread instruction and a
 * published page. The mode decides what the model is *offered* and which of those stop; this
 * is where that decision is actually enforced.
 */

const author = {
  isSuperAdmin: false,
  permissions: ['content:read', 'content:write', 'content:publish'],
};

function wrapper({ children }: { children: React.ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <QueryClientProvider client={client}>
      <PermissionProvider value={author}>{children}</PermissionProvider>
    </QueryClientProvider>
  );
}

const options = { conversationId: null, onConversationChange: () => {} };

/** The tool names the model was offered on the given turn. */
const offeredOn = (turn: number) =>
  ((vi.mocked(streamAssistantTurn).mock.calls[turn][0].tools ?? []) as { name: string }[]).map(
    (t) => t.name,
  );

/** One assistant turn asking for `name`, then one that just answers. */
function turns(name: string, input: Record<string, unknown>) {
  const call = { type: 'tool_use' as const, id: 'call-1', name, input };
  vi.mocked(streamAssistantTurn)
    .mockResolvedValueOnce({ content: [call], stopReason: 'tool_use' } as never)
    .mockResolvedValue({
      content: [{ type: 'text', text: 'done' }],
      stopReason: 'end_turn',
    } as never);
}

const draft = () =>
  turns('create_content', {
    instanceId: 'i',
    contentType: 'article',
    slug: 'a',
    data: { title: 'A' },
  });

const publish = () => turns('publish_content', { id: 'item-1' });

/** Writes go through api.post; the conversation itself is created with the same verb. */
const contentPosts = () =>
  vi.mocked(api.post).mock.calls.filter(([path]) => String(path).startsWith('/admin/content'));

beforeEach(() => {
  vi.resetAllMocks();
  localStorage.clear();
  vi.mocked(api.post).mockResolvedValue({ id: 'conv-1' });
});

describe('agent mode', () => {
  it('drafts without asking — that is the whole point of it', async () => {
    // Asking permission to save a draft teaches an operator to click Allow without reading,
    // which is exactly the habit you do not want when the card says "publish".
    draft();
    const { result } = renderHook(() => useAssistantSession(null, options), { wrapper });

    act(() => result.current.send('write me a post'));
    await waitFor(() => expect(contentPosts()).toHaveLength(1));
    expect(result.current.pending).toBeNull();
  });

  it('stops before publishing', async () => {
    publish();
    const { result } = renderHook(() => useAssistantSession(null, options), { wrapper });

    act(() => result.current.send('publish it'));
    await waitFor(() => expect(result.current.pending).not.toBeNull());
    expect(contentPosts()).toHaveLength(0);
    expect(result.current.pending!.dangerous).toBe(true);

    act(() => result.current.approve(true));
    await waitFor(() => expect(contentPosts()).toHaveLength(1));
  });

  it('never calls the API when the operator declines, and tells the model so', async () => {
    publish();
    const { result } = renderHook(() => useAssistantSession(null, options), { wrapper });

    act(() => result.current.send('publish it'));
    await waitFor(() => expect(result.current.pending).not.toBeNull());
    act(() => result.current.approve(false));

    await waitFor(() => expect(result.current.running).toBe(false));
    expect(contentPosts()).toHaveLength(0);
    const [, second] = vi.mocked(streamAssistantTurn).mock.calls;
    expect(JSON.stringify(second[0].messages)).toContain('declined');
  });

  it('resolves a waiting approval as a refusal when the run is stopped', async () => {
    // Otherwise the loop sits forever on a promise behind a card that is no longer rendered.
    publish();
    const { result } = renderHook(() => useAssistantSession(null, options), { wrapper });

    act(() => result.current.send('publish it'));
    await waitFor(() => expect(result.current.pending).not.toBeNull());
    act(() => result.current.stop());

    await waitFor(() => expect(result.current.pending).toBeNull());
    expect(contentPosts()).toHaveLength(0);
  });
});

describe('ask-first mode', () => {
  it('stops even for a draft', async () => {
    draft();
    const { result } = renderHook(() => useAssistantSession(null, options), { wrapper });

    act(() => result.current.setMode('careful'));
    act(() => result.current.send('write me a post'));
    await waitFor(() => expect(result.current.pending).not.toBeNull());
    expect(result.current.pending!.dangerous).toBe(false);
    expect(contentPosts()).toHaveLength(0);
  });
});

describe('full auto', () => {
  it('publishes without stopping', async () => {
    publish();
    const { result } = renderHook(() => useAssistantSession(null, options), { wrapper });

    act(() => result.current.setMode('auto'));
    act(() => result.current.send('publish it'));
    await waitFor(() => expect(contentPosts()).toHaveLength(1));
    expect(result.current.pending).toBeNull();
  });

  it('is not remembered for the next session', () => {
    const { result } = renderHook(() => useAssistantSession(null, options), { wrapper });
    act(() => result.current.setMode('auto'));
    expect(localStorage.getItem('dcms.ai.mode')).toBe('agent');
  });
});

describe('always-allow', () => {
  it('stops asking for that tool for the rest of the conversation', async () => {
    // Without it, the only way out of twelve identical cards in a bulk job is Full auto —
    // a far bigger yes than the operator meant to give.
    const call = (id: string) => ({
      type: 'tool_use' as const,
      id,
      name: 'publish_content',
      input: { id: 'item-1' },
    });
    vi.mocked(streamAssistantTurn)
      .mockResolvedValueOnce({ content: [call('c1')], stopReason: 'tool_use' } as never)
      .mockResolvedValueOnce({ content: [call('c2')], stopReason: 'tool_use' } as never)
      .mockResolvedValue({
        content: [{ type: 'text', text: 'done' }],
        stopReason: 'end_turn',
      } as never);

    const { result } = renderHook(() => useAssistantSession(null, options), { wrapper });
    act(() => result.current.send('publish both'));
    await waitFor(() => expect(result.current.pending).not.toBeNull());

    act(() => result.current.approve(true, true));
    await waitFor(() => expect(result.current.running).toBe(false));
    expect(contentPosts()).toHaveLength(2);
    expect(result.current.pending).toBeNull();
  });
});

describe('read-only mode', () => {
  it('offers the model no writing tool at all', async () => {
    vi.mocked(streamAssistantTurn).mockResolvedValue({
      content: [{ type: 'text', text: 'hi' }],
      stopReason: 'end_turn',
    } as never);
    const { result } = renderHook(() => useAssistantSession(null, options), { wrapper });

    act(() => result.current.setMode('read'));
    act(() => result.current.send('hello'));
    await waitFor(() => expect(streamAssistantTurn).toHaveBeenCalled());
    expect(offeredOn(0)).not.toContain('create_content');
  });
});

describe('a reader', () => {
  it('is held in read mode, even with a remembered agent mode', () => {
    localStorage.setItem('dcms.ai.mode', 'agent');
    const reader = { isSuperAdmin: false, permissions: ['content:read'] };
    const { result } = renderHook(() => useAssistantSession(null, options), {
      wrapper: ({ children }) => (
        <QueryClientProvider client={new QueryClient()}>
          <PermissionProvider value={reader}>{children}</PermissionProvider>
        </QueryClientProvider>
      ),
    });
    expect(result.current.writable).toBe(false);
    expect(result.current.mode).toBe('read');
  });
});

describe('the stored conversation', () => {
  it('creates one on the first question and appends every turn to it', async () => {
    // A transcript that only exists in React state loses the reasoning behind whatever the
    // agent just wrote to the workspace, the moment anyone reloads.
    draft();
    const changed = vi.fn();
    const { result } = renderHook(
      () => useAssistantSession(null, { conversationId: null, onConversationChange: changed }),
      { wrapper },
    );

    act(() => result.current.send('write me a post'));
    await waitFor(() => expect(result.current.running).toBe(false));

    expect(api.post).toHaveBeenCalledWith(
      '/admin/ai/conversations',
      expect.objectContaining({ title: 'write me a post', mode: 'agent' }),
    );
    expect(changed).toHaveBeenCalledWith('conv-1');
    expect(
      vi
        .mocked(api.post)
        .mock.calls.filter(([p]) => p === '/admin/ai/conversations/conv-1/messages').length,
    ).toBeGreaterThan(0);
  });

  it('keeps answering when the history cannot be saved', async () => {
    // The operator is mid-answer. Losing the archive is worth a line in the composer, never
    // an interruption.
    vi.mocked(api.post).mockImplementation((path: string) =>
      path === '/admin/ai/conversations'
        ? Promise.resolve({ id: 'conv-1' })
        : Promise.reject(new Error('nope')),
    );
    vi.mocked(streamAssistantTurn).mockResolvedValue({
      content: [{ type: 'text', text: 'still answered' }],
      stopReason: 'end_turn',
    } as never);

    const { result } = renderHook(() => useAssistantSession(null, options), { wrapper });
    act(() => result.current.send('hello'));
    await waitFor(() => expect(result.current.running).toBe(false));

    expect(result.current.unsaved).toBe(true);
    expect(result.current.steps.some((s) => s.kind === 'assistant')).toBe(true);
  });
});

describe("somebody else's conversation", () => {
  it('is readable, and refuses to be continued', async () => {
    // The server refuses the append anyway, but a composer that accepts a question and then
    // runs tools against work the reader did not do is the wrong shape regardless.
    vi.mocked(api.get).mockResolvedValue({
      id: 'conv-9',
      title: 'their work',
      visibility: 'Workspace',
      mode: 'agent',
      mine: false,
      messages: [{ id: 'm1', seq: 1, role: 'user', content: [{ type: 'text', text: 'hello' }] }],
    });

    const { result } = renderHook(
      () => useAssistantSession(null, { conversationId: 'conv-9', onConversationChange: () => {} }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.readOnly).toBe(true));
    expect(result.current.steps).toHaveLength(1);

    act(() => result.current.send('carry on then'));
    expect(streamAssistantTurn).not.toHaveBeenCalled();
  });
});

describe('the transcript', () => {
  it('records what each call did, not just that it happened', async () => {
    draft();
    vi.mocked(api.post).mockImplementation((path: string) =>
      Promise.resolve(path === '/admin/ai/conversations' ? { id: 'conv-1' } : { id: 'new-item' }),
    );
    const { result } = renderHook(() => useAssistantSession(null, options), { wrapper });

    act(() => result.current.send('write me a post'));
    await waitFor(() => expect(result.current.running).toBe(false));

    const card = result.current.steps.find((s): s is ToolStep => s.kind === 'tool')!;
    expect(card.label).toBe('Created draft a');
    expect(card.status).toBe('ok');
    expect(card.input).toMatchObject({ slug: 'a' });
    expect(card.result).toContain('new-item');
  });

  it('marks a failed call as failed and keeps the run going', async () => {
    draft();
    vi.mocked(api.post).mockImplementation((path: string) =>
      path === '/admin/ai/conversations'
        ? Promise.resolve({ id: 'conv-1' })
        : Promise.reject(new Error('the collection is gone')),
    );
    const { result } = renderHook(() => useAssistantSession(null, options), { wrapper });

    act(() => result.current.send('write me a post'));
    await waitFor(() => expect(result.current.running).toBe(false));

    const card = result.current.steps.find((s): s is ToolStep => s.kind === 'tool')!;
    expect(card.status).toBe('error');
    expect(card.error).toBe('the collection is gone');
  });
});
