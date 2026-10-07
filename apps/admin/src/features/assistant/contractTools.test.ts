import { describe, expect, it, vi } from 'vitest';
import { decide } from '../agent/modes';
import { api } from '../../lib/api';
import { brief, contractTools, toolName, toolRisk, type CatalogContract } from './contractTools';
import { toolsFor } from './tools';

vi.mock('../../lib/api', () => ({ api: { post: vi.fn(), get: vi.fn() } }));

const catalog: CatalogContract[] = [
  {
    id: 'visitors.profiles@1',
    description: 'Visitor profiles',
    providers: ['visitor-auth'],
    instances: [{ id: 'i1', slug: 'members', name: 'Members', description: 'Site accounts' }],
    operations: [
      {
        name: 'Get',
        description: 'A visitor profile',
        risk: 0,
        returnsExternalText: true,
        inputSchema: { type: 'object', properties: { visitorId: { type: 'string' } }, required: ['visitorId'] },
      },
      {
        name: 'SetAttributes',
        description: 'Set attributes',
        risk: 1,
        returnsExternalText: false,
        inputSchema: { type: 'object', properties: {} },
      },
    ],
  },
  {
    id: 'dcms.search@1',
    instances: [],
    operations: [{ name: 'Search', risk: 'Read', returnsExternalText: false, inputSchema: { type: 'object' } }],
  },
];

describe('contract tools', () => {
  it('names one tool per operation per instance, and platform ones by contract', () => {
    expect(contractTools(catalog).map((t) => t.name)).toEqual([
      'members_profiles_get',
      'members_profiles_set_attributes',
      'platform_dcms_search_search',
    ]);
  });

  it('carries the risk into the mode table: reads run, safe writes stop in careful mode', () => {
    const [get, set] = contractTools(catalog);
    expect(get!.risk).toBeUndefined();
    expect(set!.risk).toBe('safe');
    expect(decide('careful', set!.risk ?? 'read')).toBe('approve');
    expect(toolsFor(undefined, 'read', contractTools(catalog)).map((t) => t.name)).not.toContain(
      'members_profiles_set_attributes',
    );
  });

  it('marks operations returning visitor-written text as untrusted', () => {
    const [get, set] = contractTools(catalog);
    expect(get!.untrustedSource).toContain('site visitors');
    expect(set!.untrustedSource).toBeUndefined();
  });

  it('calls the admin dispatcher on the AI plane for the right instance', async () => {
    vi.mocked(api.post).mockResolvedValue({ email: 'a@b.test' });
    const [get] = contractTools(catalog);
    await get!.run({ visitorId: 'v1' }, { attachments: [], onUploaded: () => {} });
    expect(api.post).toHaveBeenCalledWith(
      '/admin/contracts/visitors.profiles%401/Get?plane=ai&instance=members',
      { visitorId: 'v1' },
      { headers: {} },
    );
  });

  it('sends the conversation, turn and tool call so the server can link what it changed', async () => {
    vi.mocked(api.post).mockResolvedValue({});
    const [get] = contractTools(catalog);
    await get!.run({ visitorId: 'v1' }, {
      attachments: [],
      onUploaded: () => {},
      trace: { conversationId: 'c1', runId: 'r1', toolCallId: 'toolu_1' },
    });
    expect(vi.mocked(api.post).mock.calls.at(-1)![2]).toEqual({
      headers: { 'X-Dcms-Ai-Conversation': 'c1', 'X-Dcms-Ai-Run': 'r1', 'X-Dcms-Ai-Tool-Call': 'toolu_1' },
    });
  });

  it('says on the approval card what a call will do, and refreshes the instance afterwards', () => {
    const [, set] = contractTools(catalog);
    expect(set!.invalidates).toEqual(['plugin:members']);
    expect(brief({ operations: [{ op: 'create', type: 'table', value: { apiName: 'deals' } }, { op: 'delete', type: 'field', target: 'deals.old' }] }))
      .toBe(': create table deals; delete field deals.old');
    expect(brief({})).toBe('');
  });

  it('keeps names inside provider limits', () => {
    expect(toolName('a'.repeat(80), 'GetThing')).toHaveLength(64);
    expect(toolRisk('Dangerous')).toBe('dangerous');
  });
});
