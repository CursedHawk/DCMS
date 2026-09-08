import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PermissionProvider } from '@dcms/ui';
import { streamAssistantTurn } from '../ide/agent/client';
import { api } from '../../lib/api';
import { useAssistantSession } from './useAssistantSession';

vi.mock('../ide/agent/client', async (original) => ({
  ...(await original<typeof import('../ide/agent/client')>()),
  streamAssistantTurn: vi.fn(),
}));
vi.mock('../../lib/api', () => ({ api: { get: vi.fn(), post: vi.fn(), put: vi.fn() } }));

/**
 * The approval gate, which is the only thing standing between a misread instruction and a
 * published page. The access switch decides what the model is *offered*; this decides what
 * actually runs, and it has to hold even though the tool was legitimately offered.
 */

const author = { isSuperAdmin: false, permissions: ['content:read', 'content:write'] };

function wrapper({ children }: { children: React.ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <QueryClientProvider client={client}>
      <PermissionProvider value={author}>{children}</PermissionProvider>
    </QueryClientProvider>
  );
}

/** The tool names the model was offered on the given turn. */
const offeredOn = (turn: number) =>
  ((vi.mocked(streamAssistantTurn).mock.calls[turn][0].tools ?? []) as { name: string }[]).map(
    (t) => t.name,
  );

/** One assistant turn asking to create a draft, then one that just answers. */
function turns() {
  const call = {
    type: 'tool_use' as const,
    id: 'call-1',
    name: 'create_content',
    input: { instanceId: 'i', contentType: 'article', slug: 'a', data: { title: 'A' } },
  };
  vi.mocked(streamAssistantTurn)
    .mockResolvedValueOnce({ content: [call], stopReason: 'tool_use' } as never)
    .mockResolvedValue({ content: [{ type: 'text', text: 'done' }], stopReason: 'end_turn' } as never);
}

describe('the write approval gate', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    localStorage.setItem('dcms.ai.access', 'write');
  });

  it('does not run the write until the operator allows it', async () => {
    turns();
    const { result } = renderHook(() => useAssistantSession(null), { wrapper });

    act(() => result.current.send('write me a post'));
    await waitFor(() => expect(result.current.pending).not.toBeNull());
    expect(api.post).not.toHaveBeenCalled();

    // The card shows the arguments themselves, not a paraphrase of them.
    expect(result.current.pending!.payload).toContain('"slug": "a"');

    act(() => result.current.approve(true));
    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(1));
  });

  it('never calls the API when the operator declines, and tells the model so', async () => {
    turns();
    const { result } = renderHook(() => useAssistantSession(null), { wrapper });

    act(() => result.current.send('write me a post'));
    await waitFor(() => expect(result.current.pending).not.toBeNull());
    act(() => result.current.approve(false));

    await waitFor(() => expect(result.current.running).toBe(false));
    expect(api.post).not.toHaveBeenCalled();
    const [, second] = vi.mocked(streamAssistantTurn).mock.calls;
    expect(JSON.stringify(second[0].messages)).toContain('declined');
  });

  it('resolves a waiting approval as a refusal when the run is stopped', async () => {
    // Otherwise the loop sits forever on a promise behind a card that is no longer rendered.
    turns();
    const { result } = renderHook(() => useAssistantSession(null), { wrapper });

    act(() => result.current.send('write me a post'));
    await waitFor(() => expect(result.current.pending).not.toBeNull());
    act(() => result.current.stop());

    await waitFor(() => expect(result.current.pending).toBeNull());
    expect(api.post).not.toHaveBeenCalled();
  });
});

describe('access mode', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    localStorage.removeItem('dcms.ai.access');
  });

  it('starts read-only and offers the model no writing tool', async () => {
    vi.mocked(streamAssistantTurn).mockResolvedValue({
      content: [{ type: 'text', text: 'hi' }],
      stopReason: 'end_turn',
    } as never);
    const { result } = renderHook(() => useAssistantSession(null), { wrapper });
    expect(result.current.mode).toBe('read');

    act(() => result.current.send('hello'));
    await waitFor(() => expect(streamAssistantTurn).toHaveBeenCalled());
    expect(offeredOn(0)).not.toContain('create_content');
  });

  it('offers the writing tools once the switch is on, and remembers it', async () => {
    vi.mocked(streamAssistantTurn).mockResolvedValue({
      content: [{ type: 'text', text: 'hi' }],
      stopReason: 'end_turn',
    } as never);
    const { result } = renderHook(() => useAssistantSession(null), { wrapper });

    act(() => result.current.setMode('write'));
    act(() => result.current.send('hello'));
    await waitFor(() => expect(streamAssistantTurn).toHaveBeenCalled());
    expect(offeredOn(0)).toContain('create_content');
    expect(localStorage.getItem('dcms.ai.access')).toBe('write');
  });
});

describe('a reader', () => {
  it('is never shown the switch, even with a remembered write mode', () => {
    localStorage.setItem('dcms.ai.access', 'write');
    const reader = { isSuperAdmin: false, permissions: ['content:read'] };
    const { result } = renderHook(() => useAssistantSession(null), {
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
