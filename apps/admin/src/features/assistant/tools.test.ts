import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { MyPermissions } from '@dcms/core';
import {
  ASSISTANT_TOOLS,
  canUseWriteAccess,
  toolDefinitions,
  toolsFor,
} from './tools';
import { api } from '../../lib/api';

vi.mock('../../lib/api', () => ({
  api: { get: vi.fn(), post: vi.fn(), put: vi.fn() },
}));

const tool = (name: string) => ASSISTANT_TOOLS.find((t) => t.name === name)!;

const holder = (...permissions: string[]): MyPermissions => ({ isSuperAdmin: false, permissions });

describe('toolsFor', () => {
  it('offers a tool the caller holds the permission for', () => {
    expect(toolsFor(holder('analytics:read')).map((t) => t.name)).toContain('get_analytics');
  });

  it('does NOT offer a tool the caller cannot use', () => {
    // Absent from the list, not refused at call time. A model told a tool exists will reach
    // for it, and an assistant that keeps announcing it cannot do what it just offered reads
    // as broken rather than as careful.
    expect(toolsFor(holder('analytics:read')).map((t) => t.name)).not.toContain('search_media');
  });

  it('offers nothing before permissions have loaded', () => {
    expect(toolsFor(undefined)).toEqual([]);
  });

  it('offers a SuperAdmin everything that reads, and no more, in read mode', () => {
    const offered = toolsFor({ isSuperAdmin: true, permissions: [] });
    expect(offered).toHaveLength(ASSISTANT_TOOLS.filter((t) => !t.mutates).length);
    expect(offered.every((t) => !t.mutates)).toBe(true);
  });

  it('offers a SuperAdmin everything in write mode', () => {
    expect(toolsFor({ isSuperAdmin: true, permissions: [] }, 'write')).toHaveLength(
      ASSISTANT_TOOLS.length,
    );
  });

  it('offers nothing to a member holding no relevant permission', () => {
    expect(toolsFor(holder('audit:read'))).toEqual([]);
  });
});

describe('the tool catalogue', () => {
  it('gates every tool on a permission', () => {
    // An ungated tool is one the assistant would offer to anybody who can sign in.
    for (const tool of ASSISTANT_TOOLS) {
      expect(tool.permission, `${tool.name} names no permission`).toBeTruthy();
    }
  });

  it('makes every mutating tool explain itself and refresh what it changed', () => {
    // The summary is what the operator actually approves against, and a write that leaves the
    // list behind the dock showing the old world reads as a write that did not happen.
    for (const t of ASSISTANT_TOOLS.filter((t) => t.mutates)) {
      expect(t.summarize, `${t.name} has no summary`).toBeTruthy();
      expect(t.invalidates?.length, `${t.name} invalidates nothing`).toBeGreaterThan(0);
    }
  });

  it('gives every tool a description the model can act on', () => {
    for (const tool of ASSISTANT_TOOLS) {
      expect(tool.description.length).toBeGreaterThan(30);
    }
  });

  it('uses unique names', () => {
    const names = ASSISTANT_TOOLS.map((t) => t.name);
    expect(new Set(names).size).toBe(names.length);
  });

  it('declares closed input schemas, so the model cannot invent parameters', () => {
    for (const tool of ASSISTANT_TOOLS) {
      expect(tool.input_schema.additionalProperties, `${tool.name}`).toBe(false);
    }
  });
});

describe('toolDefinitions', () => {
  it('sends the model the schema and nothing else', () => {
    const [first] = toolDefinitions(ASSISTANT_TOOLS.slice(0, 1));
    expect(Object.keys(first).sort()).toEqual(['description', 'input_schema', 'name']);
    expect(first).not.toHaveProperty('run');
    expect(first).not.toHaveProperty('permission');
  });
});


describe('access mode', () => {
  it('offers no mutating tool in read mode, whatever the caller may do', () => {
    const offered = toolsFor(holder('content:read', 'content:write', 'content:publish'));
    expect(offered.every((t) => !t.mutates)).toBe(true);
  });

  it('offers the writing tools in write mode', () => {
    const names = toolsFor(holder('content:write'), 'write').map((t) => t.name);
    expect(names).toContain('create_content');
    expect(names).toContain('update_content');
  });

  it('still withholds publishing from someone who may write but not publish', () => {
    // The mode is not a bypass: it decides which *permitted* tools are offered.
    const names = toolsFor(holder('content:write'), 'write').map((t) => t.name);
    expect(names).not.toContain('publish_content');
  });
});

describe('canUseWriteAccess', () => {
  it('is false for a reader — the switch is not shown at all', () => {
    expect(canUseWriteAccess(holder('content:read', 'media:read'))).toBe(false);
  });

  it('is true for an author', () => {
    expect(canUseWriteAccess(holder('content:write'))).toBe(true);
  });

  it('is false before permissions have loaded', () => {
    expect(canUseWriteAccess(undefined)).toBe(false);
  });
});

describe('the writing tools', () => {
  beforeEach(() => vi.resetAllMocks());

  it('creates a draft under the collection the model named', async () => {
    vi.mocked(api.post).mockResolvedValue({ id: 'new' });
    await tool('create_content').run({
      instanceId: 'inst-1',
      contentType: 'article',
      slug: 'hello-world',
      data: { title: 'Hello' },
    });
    expect(api.post).toHaveBeenCalledWith('/admin/content', {
      pluginInstanceId: 'inst-1',
      contentType: 'article',
      slug: 'hello-world',
      data: { title: 'Hello' },
    });
  });

  it('merges an update over the current draft rather than replacing it', async () => {
    // PUT replaces the draft wholesale. A model asked to fix one field sends one field, and
    // posting that alone would empty every other field of the item.
    vi.mocked(api.get).mockResolvedValue({ draft: { title: 'Old', body: 'Kept' } });
    vi.mocked(api.put).mockResolvedValue({ versionNo: 2 });
    await tool('update_content').run({ id: 'item-1', data: { title: 'New' } });
    expect(api.put).toHaveBeenCalledWith('/admin/content/item-1', {
      data: { title: 'New', body: 'Kept' },
    });
  });

  it('survives an item that has no draft yet', async () => {
    vi.mocked(api.get).mockResolvedValue({ draft: null });
    vi.mocked(api.put).mockResolvedValue({});
    await tool('update_content').run({ id: 'item-1', data: { title: 'First' } });
    expect(api.put).toHaveBeenCalledWith('/admin/content/item-1', { data: { title: 'First' } });
  });

  it('refuses a call with no id rather than requesting /content/undefined', async () => {
    await expect(tool('publish_content').run({})).rejects.toThrow(/required/);
    expect(api.post).not.toHaveBeenCalled();
  });

  it('says what publishing will do, in the operator\'s terms', () => {
    expect(tool('publish_content').summarize!({ id: 'item-1' })).toMatch(/public/);
  });
});
