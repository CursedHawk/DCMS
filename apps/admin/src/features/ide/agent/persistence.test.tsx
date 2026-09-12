import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PermissionProvider } from '@dcms/ui';
import { streamAssistantTurn } from './client';
import { api } from '../../../lib/api';
import { useVfs } from '../../site-source/vfs';
import { useAgentSession } from './useAgentSession';

vi.mock('./client', async (original) => ({
  ...(await original<typeof import('./client')>()),
  streamAssistantTurn: vi.fn(),
}));
vi.mock('../../../lib/api', () => ({
  api: { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), del: vi.fn(), upload: vi.fn() },
}));
// The preview lives in another pane and owns an esbuild worker; a session test has neither.
vi.mock('../preview/buildBroker', () => ({
  previewAvailable: () => false,
  requestBuild: async () => null,
}));

/**
 * What a closed tab leaves behind.
 *
 * <p>The agent loop runs in the browser (D1), so nothing server-side can close a run out. The
 * only thing that makes a run readable afterwards is storing turns *as they happen* — and the
 * failure mode if that is wrong is silent: the run looks fine on screen and the history is
 * simply missing, which nobody discovers until they go looking for it.</p>
 */

const editor = { isSuperAdmin: false, permissions: ['site:edit'] };

/** The one readable string an entry carries, whatever kind it is. */
function entryText(entry: import('./useAgentSession').AgentEntry): string {
  return entry.kind === 'tool' ? entry.name : 'text' in entry ? entry.text : entry.kind;
}

function wrapper({ children }: { children: React.ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <QueryClientProvider client={client}>
      <PermissionProvider value={editor}>{children}</PermissionProvider>
    </QueryClientProvider>
  );
}

/** Every append the session made, in order, flattened to `{ role, seq-ish }` shape. */
function appends() {
  return vi
    .mocked(api.post)
    .mock.calls.filter(([path]) => String(path).endsWith('/messages'))
    .map(([, body]) => body as { messages: { role: string }[] });
}

function runRecords() {
  return vi
    .mocked(api.put)
    .mock.calls.filter(([path]) => String(path).includes('/runs/'))
    .map(([path, body]) => ({ path: String(path), body: body as Record<string, unknown> }));
}

let seq = 0;

beforeEach(() => {
  vi.clearAllMocks();
  seq = 0;
  useVfs.setState({ files: { 'src/App.tsx': 'x' }, branch: 'main' });

  vi.mocked(api.post).mockImplementation(async (path: string) => {
    if (path.endsWith('/messages')) return { messageCount: ++seq, lastSeq: ++seq } as never;
    return { id: 'conv-1' } as never;
  });
  vi.mocked(api.put).mockResolvedValue({ id: 'run-1' } as never);
});

/** One assistant turn that just answers. */
function answers(text = 'done') {
  vi.mocked(streamAssistantTurn).mockResolvedValue({
    content: [{ type: 'text', text }],
    stopReason: 'end_turn',
  } as never);
}

describe('the IDE agent stores as it goes', () => {
  it('opens a conversation before the first model call', async () => {
    // A conversation created only once a turn succeeds would lose exactly the runs worth
    // keeping: the ones where the first call failed.
    vi.mocked(streamAssistantTurn).mockRejectedValue(new Error('provider down'));

    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    act(() => result.current.send('add a page'));
    await waitFor(() => expect(result.current.running).toBe(false));

    const created = vi.mocked(api.post).mock.calls.find(([p]) =>
      String(p) === '/admin/ai/conversations',
    );
    expect(created?.[1]).toMatchObject({ surface: 'ide', siteId: 'site-1', branch: 'main' });
  });

  it('records the run before the first model call too', async () => {
    vi.mocked(streamAssistantTurn).mockRejectedValue(new Error('provider down'));

    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    act(() => result.current.send('add a page'));
    await waitFor(() => expect(result.current.running).toBe(false));
    await waitFor(() => expect(runRecords().length).toBeGreaterThan(0));

    // The opening half carries the task and no `finished`, so a tab closed here leaves a row
    // that reads as "this run never finished" rather than leaving nothing at all.
    expect(runRecords()[0].body).toMatchObject({ task: 'add a page' });
    expect(runRecords()[0].body.finished).toBeUndefined();
  });

  it('stores the question even when the model never answers', async () => {
    vi.mocked(streamAssistantTurn).mockRejectedValue(new Error('provider down'));

    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    act(() => result.current.send('add a page'));
    await waitFor(() => expect(result.current.running).toBe(false));
    await waitFor(() => expect(appends().length).toBeGreaterThan(0));

    expect(appends()[0].messages[0]).toMatchObject({ role: 'user' });
  });

  it('closes the run record with its outcome', async () => {
    answers();
    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    act(() => result.current.send('say hello'));
    await waitFor(() => expect(result.current.running).toBe(false));
    await waitFor(() => expect(runRecords().some((r) => r.body.finished === true)).toBe(true));

    const closing = runRecords().at(-1)!;
    expect(closing.body).toMatchObject({ outcome: 'completed', finished: true });
    // Same id both halves, or the two would be separate rows and the opening one would stay
    // open forever.
    expect(closing.path).toBe(runRecords()[0].path);
  });

  it('never appends the same turn twice', async () => {
    // `persistedUpTo` advances only on a successful append. Getting that wrong duplicates every
    // turn in the stored transcript, which no screen would show and every resume would repeat.
    answers();
    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    act(() => result.current.send('say hello'));
    await waitFor(() => expect(result.current.running).toBe(false));
    await waitFor(() => expect(appends().length).toBeGreaterThan(0));

    const roles = appends().flatMap((a) => a.messages.map((m) => m.role));
    expect(roles.filter((r) => r === 'user')).toHaveLength(1);
    expect(roles.filter((r) => r === 'assistant')).toHaveLength(1);
  });

  it('re-sends a turn whose append failed rather than leaving a hole', async () => {
    answers();
    let calls = 0;
    vi.mocked(api.post).mockImplementation(async (path: string) => {
      if (!path.endsWith('/messages')) return { id: 'conv-1' } as never;
      if (++calls === 1) throw new Error('history server down');
      return { messageCount: 2, lastSeq: 2 } as never;
    });

    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    act(() => result.current.send('say hello'));
    await waitFor(() => expect(result.current.running).toBe(false));
    await waitFor(() => expect(appends().length).toBeGreaterThan(1));

    // The second attempt carries the user turn the first one failed to store.
    expect(appends()[1].messages.map((m) => m.role)).toContain('user');
  });

  it('says so when a turn could not be stored', async () => {
    answers();
    vi.mocked(api.post).mockImplementation(async (path: string) => {
      if (path.endsWith('/messages')) throw new Error('history server down');
      return { id: 'conv-1' } as never;
    });

    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    act(() => result.current.send('say hello'));
    await waitFor(() => expect(result.current.unsaved).toBe(true));
    // …and the run itself still finished. A history problem must not break the work.
    expect(result.current.running).toBe(false);
  });

  it('still stores the transcript when the run record cannot be written', async () => {
    // The two are separate writes on purpose. The run record is a summary; the transcript is
    // the record of an agent writing to a workspace, and losing the second because the first
    // failed would be the wrong way round.
    answers();
    vi.mocked(api.put).mockRejectedValue(new Error('nope'));

    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    act(() => result.current.send('say hello'));
    await waitFor(() => expect(result.current.running).toBe(false));
    await waitFor(() => expect(appends().length).toBeGreaterThan(0));

    expect(result.current.unsaved).toBe(false);
    expect(appends().flatMap((a) => a.messages).some((m) => m.role === 'assistant')).toBe(true);
  });

  it('drains a backlog in bounded batches rather than one oversized request', async () => {
    /*
     * The failure this prevents is permanent, not transient. Each failed flush leaves its turns
     * pending, so an outage during a tool-heavy run accumulates them until one request would
     * carry the lot — and be refused by the server's 1 MB cap, at which point the transcript is
     * stuck for good rather than merely behind.
     */
    const huge = 'x'.repeat(400 * 1024);
    vi.mocked(streamAssistantTurn)
      .mockResolvedValueOnce({
        content: [{ type: 'tool_use', id: 't1', name: 'read_file', input: { path: huge } }],
        stopReason: 'tool_use',
      } as never)
      .mockResolvedValue({
        content: [{ type: 'text', text: huge }],
        stopReason: 'end_turn',
      } as never);

    // Nothing stores until the very end, so the whole run is one backlog by the time it drains.
    let allowed = false;
    vi.mocked(api.post).mockImplementation(async (path: string) => {
      if (!path.endsWith('/messages')) return { id: 'conv-1' } as never;
      if (!allowed) throw new Error('history server down');
      return { messageCount: 1, lastSeq: ++seq } as never;
    });

    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    act(() => result.current.send(huge));
    await waitFor(() => expect(result.current.running).toBe(false));

    // The history server comes back. The next run's flushes drain the backlog.
    allowed = true;
    act(() => result.current.send('and again'));
    await waitFor(() => expect(result.current.running).toBe(false));
    await waitFor(() => expect(result.current.unsaved).toBe(false));

    const sizes = appends().map((a) => JSON.stringify(a.messages).length);
    expect(sizes.length).toBeGreaterThan(1);
    // Every batch that carried more than one turn stayed under the budget.
    for (const [i, size] of sizes.entries()) {
      if (appends()[i].messages.length > 1) expect(size).toBeLessThan(512 * 1024 + 4096);
    }
  });

  it('detaches from the conversation when a new one is started', async () => {
    answers();
    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    act(() => result.current.send('first'));
    await waitFor(() => expect(result.current.conversationId).toBe('conv-1'));

    act(() => result.current.newConversation());
    expect(result.current.conversationId).toBeNull();
    expect(result.current.entries).toHaveLength(0);
  });
});

describe('resuming a stored conversation', () => {
  it('puts the stored blocks back into the model history and onto the screen', async () => {
    vi.mocked(api.get).mockResolvedValue({
      messages: [
        { seq: 1, role: 'user', content: [{ type: 'text', text: 'add an About page' }] },
        { seq: 2, role: 'assistant', content: [{ type: 'text', text: 'Added it.' }] },
      ],
    } as never);

    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    await act(() => result.current.resume('conv-9'));

    expect(result.current.conversationId).toBe('conv-9');
    expect(result.current.entries.map(entryText)).toEqual(['add an About page', 'Added it.']);
  });

  it('continues the resumed conversation rather than opening a new one', async () => {
    vi.mocked(api.get).mockResolvedValue({
      messages: [{ seq: 1, role: 'user', content: [{ type: 'text', text: 'hi' }] }],
    } as never);
    answers();

    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    await act(() => result.current.resume('conv-9'));
    act(() => result.current.send('and now a Contact page'));
    await waitFor(() => expect(result.current.running).toBe(false));

    expect(
      vi.mocked(api.post).mock.calls.some(([p]) => String(p) === '/admin/ai/conversations'),
    ).toBe(false);
  });

  it('does not re-append the turns it just loaded', async () => {
    // The resumed history is already stored. Counting it as unsaved would duplicate the whole
    // conversation on the next flush.
    vi.mocked(api.get).mockResolvedValue({
      messages: [
        { seq: 1, role: 'user', content: [{ type: 'text', text: 'hi' }] },
        { seq: 2, role: 'assistant', content: [{ type: 'text', text: 'hello' }] },
      ],
    } as never);
    answers();

    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    await act(() => result.current.resume('conv-9'));
    act(() => result.current.send('next'));
    await waitFor(() => expect(result.current.running).toBe(false));
    await waitFor(() => expect(appends().length).toBeGreaterThan(0));

    const texts = appends()
      .flatMap((a) => a.messages)
      .map((m) => JSON.stringify(m));
    expect(texts.filter((t) => t.includes('hello'))).toHaveLength(0);
  });

  it('shows a tool call as the call, not as its result', async () => {
    // A resumed transcript that pasted whole file reads back into the panel is unreadable.
    vi.mocked(api.get).mockResolvedValue({
      messages: [
        { seq: 1, role: 'user', content: [{ type: 'text', text: 'read it' }] },
        {
          seq: 2,
          role: 'assistant',
          content: [{ type: 'tool_use', id: 't1', name: 'read_file', input: {} }],
        },
        {
          seq: 3,
          role: 'user',
          content: [{ type: 'tool_result', tool_use_id: 't1', content: 'a thousand lines' }],
        },
      ],
    } as never);

    const { result } = renderHook(() => useAgentSession({ siteId: 'site-1' }), { wrapper });
    await act(() => result.current.resume('conv-9'));

    expect(result.current.entries.map(entryText)).toEqual(['read it', 'read_file']);
  });
});
