import { beforeEach, describe, expect, it, vi } from 'vitest';
import { afterEach } from 'vitest';
import { api, ApiError } from '../../lib/api';
import { streamAssistantTurn } from '../ide/agent/client';
import { requestSiteChange, sendToSite } from './host';
import { answerSite, configureSite, resetAgentHost, sessionOf, stopSite, useAgentHost } from './hostStore';

vi.mock('../ide/agent/client', async (original) => ({
  ...(await original<typeof import('../ide/agent/client')>()),
  streamAssistantTurn: vi.fn(),
}));
vi.mock('../../lib/api', async (original) => ({
  ...(await original<typeof import('../../lib/api')>()),
  api: { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), del: vi.fn(), upload: vi.fn() },
}));

/**
 * The agent host (ADR 0033): site agents that outlive their page, one per site.
 *
 * <p>Before it, the HrSite conversation had two loops on one transcript — a panel remounted
 * while the first was still running — and nothing in the console showed either.</p>
 */

/** Model turns released one at a time, so a test can hold a run open. */
function gatedModel() {
  const gates: (() => void)[] = [];
  vi.mocked(streamAssistantTurn).mockImplementation(
    () =>
      new Promise((resolve) => {
        gates.push(() => resolve({ content: [{ type: 'text', text: 'done' }], stopReason: 'end_turn' } as never));
      }),
  );
  return {
    pending: () => gates.length,
    release: () => gates.shift()?.(),
  };
}

const flushAsync = () => new Promise((r) => setTimeout(r, 0));

async function until(check: () => boolean) {
  for (let i = 0; i < 200 && !check(); i++) await flushAsync();
  expect(check()).toBe(true);
}

beforeEach(() => {
  vi.clearAllMocks();
  resetAgentHost();
  let seq = 0;
  vi.mocked(api.get).mockImplementation(async (path: string) => {
    if (path.endsWith('/ide')) return { branch: 'main', baseSha: null, version: 1, files: { 'src/App.tsx': 'x' }, hashes: {} } as never;
    if (path === '/admin/sites/static-site') return { name: 'Docs', renderMode: 'StaticFiles' } as never;
    if (path.startsWith('/admin/sites/')) return { name: 'HR portal', renderMode: 'ReactApp' } as never;
    return {} as never;
  });
  vi.mocked(api.post).mockImplementation(async (path: string) => {
    if (path.endsWith('/messages')) return { messageCount: ++seq, lastSeq: ++seq } as never;
    return { id: `conv-${++seq}` } as never;
  });
  vi.mocked(api.put).mockResolvedValue({} as never);
  vi.mocked(api.del).mockResolvedValue(undefined as never);
});

describe('agent host', () => {
  it('runs one agent per site: a second request waits for the first', async () => {
    const model = gatedModel();
    configureSite('site-a', { kind: 'react' });

    sendToSite('site-a', 'first');
    await until(() => model.pending() === 1);
    sendToSite('site-a', 'second');

    expect(sessionOf('site-a')?.queue.map((q) => q.task)).toEqual(['second']);
    expect(model.pending()).toBe(1);

    model.release();
    await until(() => model.pending() === 1 && sessionOf('site-a')?.task === 'second');
    model.release();
    await until(() => sessionOf('site-a')?.running === false);
  });

  it('runs agents on different sites at the same time', async () => {
    const model = gatedModel();
    configureSite('site-a', { kind: 'react' });
    configureSite('site-b', { kind: 'react' });

    sendToSite('site-a', 'restyle');
    sendToSite('site-b', 'add a page');
    await until(() => model.pending() === 2);
    expect(sessionOf('site-a')?.running && sessionOf('site-b')?.running).toBe(true);

    model.release();
    model.release();
    await until(() => !sessionOf('site-a')?.running && !sessionOf('site-b')?.running);
  });

  it('keeps running with no panel mounted — nothing here is a component', async () => {
    const model = gatedModel();
    configureSite('site-a', { kind: 'react' });
    sendToSite('site-a', 'work');
    await until(() => model.pending() === 1);
    model.release();
    await until(() => sessionOf('site-a')?.outcome === 'completed');
  });

  it('a stop clears what was queued, and says so to whoever asked', async () => {
    const model = gatedModel();
    configureSite('site-a', { kind: 'react' });
    sendToSite('site-a', 'first');
    await until(() => model.pending() === 1);
    const request = await requestSiteChange({ siteId: 'site-a', task: 'later', requestedBy: 'chat-1' });
    expect(useAgentHost.getState().requests[request.id].status).toBe('queued');

    stopSite('site-a');
    expect(sessionOf('site-a')?.queue).toEqual([]);
    expect(useAgentHost.getState().requests[request.id].status).toBe('stopped');
    model.release();
  });

  it("hands the assistant's request to the site's agent in a conversation of its own", async () => {
    const model = gatedModel();
    const request = await requestSiteChange({ siteId: 'site-a', task: 'make the header blue', requestedBy: 'chat-1' });

    await until(() => model.pending() === 1);
    expect(sessionOf('site-a')).toMatchObject({ kind: 'react', siteName: 'HR portal', origin: 'delegated', requestedBy: 'chat-1' });
    const created = vi.mocked(api.post).mock.calls.find(([p]) => p === '/admin/ai/conversations');
    expect(created?.[1]).toMatchObject({ surface: 'ide', siteId: 'site-a', title: 'make the header blue' });

    model.release();
    await until(() => useAgentHost.getState().requests[request.id].status === 'completed');
    expect(useAgentHost.getState().requests[request.id].summary).toBe('done');
  });

  it('refuses a site no agent can edit', async () => {
    await expect(requestSiteChange({ siteId: 'static-site', task: 'x', requestedBy: null })).rejects.toThrow(/no site agent/);
  });

  it('asks before the tab closes, only while something runs', async () => {
    const closing = () => {
      const event = new Event('beforeunload', { cancelable: true });
      window.dispatchEvent(event);
      return event.defaultPrevented;
    };
    expect(closing()).toBe(false);

    const model = gatedModel();
    configureSite('site-a', { kind: 'react' });
    sendToSite('site-a', 'work');
    await until(() => model.pending() === 1);
    expect(closing()).toBe(true);

    model.release();
    await until(() => sessionOf('site-a')?.running === false);
    expect(closing()).toBe(false);
  });


  it('waits on its questions, and the model reads the answers', async () => {
    const ask = {
      type: 'tool_use',
      id: 'q1',
      name: 'ask_user',
      input: { questions: [{ question: 'Which layout?', options: [{ label: 'Cards' }, { label: 'List' }] }] },
    };
    const sent: unknown[] = [];
    vi.mocked(streamAssistantTurn)
      .mockImplementationOnce(async () => ({ content: [ask], stopReason: 'tool_use' }) as never)
      .mockImplementationOnce(async (request) => {
        sent.push(JSON.stringify(request));
        return { content: [{ type: 'text', text: 'Cards it is.' }], stopReason: 'end_turn' } as never;
      });
    configureSite('site-a', { kind: 'react' });
    sendToSite('site-a', 'make a page');

    await until(() => sessionOf('site-a')?.question !== null && sessionOf('site-a')?.question !== undefined);
    expect(sessionOf('site-a')?.question?.questions[0].question).toBe('Which layout?');
    expect(sessionOf('site-a')?.running).toBe(true);

    answerSite('site-a', [{ selected: [], other: 'A masonry grid' }]);
    await until(() => sessionOf('site-a')?.outcome === 'completed');
    expect(sessionOf('site-a')?.question).toBeNull();
    expect(sent[0]).toContain('their own answer: \\"A masonry grid\\"');
  });

  it('a stop answers an open question, so the run can end', async () => {
    vi.mocked(streamAssistantTurn).mockImplementationOnce(
      async () =>
        ({
          content: [{ type: 'tool_use', id: 'q1', name: 'ask_user', input: { questions: [{ question: 'A or B?', options: [{ label: 'A' }, { label: 'B' }] }] } }],
          stopReason: 'tool_use',
        }) as never,
    );
    configureSite('site-a', { kind: 'react' });
    sendToSite('site-a', 'something');
    await until(() => Boolean(sessionOf('site-a')?.question));

    stopSite('site-a');
    await until(() => sessionOf('site-a')?.running === false);
    expect(sessionOf('site-a')).toMatchObject({ question: null, outcome: 'stopped' });
  });
});

/** Web Locks with real exclusivity, as two tabs of one browser see them. */
function installLocks(heldElsewhere: string[] = []) {
  const held = new Set<string>(heldElsewhere);
  Object.defineProperty(navigator, 'locks', {
    configurable: true,
    value: {
      request: async (name: string, _opts: unknown, callback: (lock: unknown) => Promise<unknown>) => {
        if (held.has(name)) return callback(null);
        held.add(name);
        try {
          return await callback({ name });
        } finally {
          held.delete(name);
        }
      },
    },
  });
}

describe('one agent per site across tabs and devices', () => {
  afterEach(() => {
    Object.defineProperty(navigator, 'locks', { configurable: true, value: undefined });
  });

  it('refuses when another tab of this browser holds the site', async () => {
    installLocks(['dcms-site-agent:site-a']);
    configureSite('site-a', { kind: 'react' });
    sendToSite('site-a', 'work');

    await until(() => sessionOf('site-a')?.outcome === 'stopped');
    expect(sessionOf('site-a')?.running).toBe(false);
    expect(JSON.stringify(sessionOf('site-a')?.entries)).toMatch(/Another tab in this browser/);
    expect(streamAssistantTurn).not.toHaveBeenCalled();
  });

  it('still starts what was queued once the first run lets go of the lock', async () => {
    // The next task must not find the finishing run's own lock and refuse itself.
    installLocks();
    const model = gatedModel();
    configureSite('site-a', { kind: 'react' });
    sendToSite('site-a', 'first');
    await until(() => model.pending() === 1);
    sendToSite('site-a', 'second');

    model.release();
    await until(() => model.pending() === 1 && sessionOf('site-a')?.task === 'second');
    model.release();
    await until(() => sessionOf('site-a')?.running === false);
    expect(JSON.stringify(sessionOf('site-a')?.entries)).not.toMatch(/Another tab/);
  });

  it('stops before the first model call when the server says another device has the site', async () => {
    vi.mocked(api.put).mockRejectedValueOnce(new ApiError(409, 'busy', { error: 'agent-busy' }));
    const request = await requestSiteChange({ siteId: 'site-a', task: 'make it blue', requestedBy: 'chat-1' });

    await until(() => useAgentHost.getState().requests[request.id].status === 'stopped');
    expect(streamAssistantTurn).not.toHaveBeenCalled();
    expect(useAgentHost.getState().requests[request.id].summary).toMatch(/another tab or on another device/);
    // The conversation it opened for nothing is taken away again.
    expect(api.del).toHaveBeenCalledWith(expect.stringMatching(/^\/admin\/ai\/conversations\/conv-/));
    expect(sessionOf('site-a')?.conversationId).toBeNull();
  });

  it('opens its run record as this tab, which is what the lease is held by', async () => {
    const model = gatedModel();
    configureSite('site-a', { kind: 'react' });
    sendToSite('site-a', 'work');
    await until(() => model.pending() === 1);

    const opening = vi.mocked(api.put).mock.calls.find(([path]) => String(path).includes('/runs/'));
    expect(opening?.[1]).toMatchObject({ task: 'work', clientId: expect.any(String) });
    model.release();
    await until(() => sessionOf('site-a')?.running === false);
  });
});
