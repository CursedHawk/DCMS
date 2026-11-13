import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../../lib/api';
import { knowledgeTools } from './knowledgeTools';

vi.mock('../../lib/api', () => ({
  api: { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), del: vi.fn(), upload: vi.fn() },
}));

const tool = (site: string | null, name: string) => knowledgeTools(site).find((t) => t.name === name)!;
const ctx = { attachments: [], onUploaded: () => {} };

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.get).mockResolvedValue({ folder: '', folders: [], entries: [] } as never);
  vi.mocked(api.put).mockImplementation(async (_path: string, body: unknown) => ({ ...(body as object), id: 'k1' }) as never);
});

describe('knowledge tools', () => {
  it("a site agent sees the workspace and its own site; the assistant sees everything", async () => {
    await tool('site-a', 'knowledge_search').run({ query: 'hero layout' }, ctx);
    expect(vi.mocked(api.get).mock.calls[0][0]).toBe('/admin/ai/knowledge?q=hero+layout&siteId=site-a');

    await tool(null, 'knowledge_list').run({ folder: 'Decisions' }, ctx);
    expect(vi.mocked(api.get).mock.calls[1][0]).toBe('/admin/ai/knowledge?folder=decisions&allSites=true');
  });

  it('a site agent saves for its site unless told the workspace, under a clean path', async () => {
    const save = tool('site-a', 'knowledge_save');
    await save.run({ path: 'Decisions/Hero Layout', title: 'Hero', description: 'Cards', body: 'Because.' }, ctx);
    expect(vi.mocked(api.put).mock.calls[0][1]).toMatchObject({ siteId: 'site-a', path: 'decisions/hero-layout', source: 'agent' });

    await save.run({ path: 'setups/fonts', title: 'Fonts', description: 'Inter', body: '-', workspace: true }, ctx);
    expect(vi.mocked(api.put).mock.calls[1][1]).toMatchObject({ siteId: null });
  });

  it('saving needs ai:knowledge; reading does not', () => {
    const byName = Object.fromEntries(knowledgeTools(null).map((t) => [t.name, t]));
    expect(byName.knowledge_save.permission).toBe('ai:knowledge');
    expect(byName.knowledge_save.risk).toBe('safe');
    expect(byName.knowledge_read.permission).toBeUndefined();
  });
});
