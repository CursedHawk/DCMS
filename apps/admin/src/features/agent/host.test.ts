import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../../lib/api';
import { streamAssistantTurn } from '../ide/agent/client';
import { requestSiteChange, sendToSite } from './host';
import { configureSite, resetAgentHost, sessionOf, stopSite, useAgentHost } from './hostStore';

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
});
