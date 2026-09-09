import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { MyPermissions } from '@dcms/core';
import { ASSISTANT_TOOLS, canWrite, toolDefinitions, toolsFor } from './tools';
import { api } from '../../lib/api';

vi.mock('../../lib/api', () => ({
  api: { get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn(), upload: vi.fn() },
}));

/** Tools take a context now; nothing below needs a real one except the upload tests. */
const noContext = { attachments: [], onUploaded: () => {} };

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
    expect(offered).toHaveLength(ASSISTANT_TOOLS.filter((t) => !t.risk).length);
    expect(offered.every((t) => !t.risk)).toBe(true);
  });

  it('offers a SuperAdmin everything in agent mode', () => {
    expect(toolsFor({ isSuperAdmin: true, permissions: [] }, 'agent')).toHaveLength(
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

  it('makes every writing tool explain itself and refresh what it changed', () => {
    // The summary is what the operator actually approves against, the description is what the
    // transcript card says afterwards, and a write that leaves the list behind the dock showing
    // the old world reads as a write that did not happen.
    for (const t of ASSISTANT_TOOLS.filter((t) => t.risk)) {
      expect(t.summarize, `${t.name} has no summary`).toBeTruthy();
      expect(t.describe, `${t.name} has no card label`).toBeTruthy();
      expect(t.invalidates?.length, `${t.name} invalidates nothing`).toBeGreaterThan(0);
    }
  });

  it('classifies every writing tool as safe or dangerous, and nothing else', () => {
    // An unclassified write would fall through `decide` as a read and run in every mode,
    // including Read only.
    for (const t of ASSISTANT_TOOLS) {
      expect(['safe', 'dangerous', undefined], `${t.name}`).toContain(t.risk);
    }
  });

  it('treats publishing, scheduling and deleting as the dangerous ones', () => {
    const dangerous = ASSISTANT_TOOLS.filter((t) => t.risk === 'dangerous').map((t) => t.name);
    expect(dangerous.sort()).toEqual([
      'delete_media',
      'publish_content',
      'schedule_content',
      'unpublish_content',
    ]);
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

describe('mode', () => {
  it('offers no writing tool in read mode, whatever the caller may do', () => {
    const offered = toolsFor(holder('content:read', 'content:write', 'content:publish'));
    expect(offered.every((t) => !t.risk)).toBe(true);
  });

  it('offers the writing tools in agent mode', () => {
    const names = toolsFor(holder('content:write'), 'agent').map((t) => t.name);
    expect(names).toContain('create_content');
    expect(names).toContain('update_content');
  });

  it('still withholds publishing from someone who may write but not publish', () => {
    // The mode is not a bypass: it decides which *permitted* tools are offered.
    const names = toolsFor(holder('content:write'), 'auto').map((t) => t.name);
    expect(names).not.toContain('publish_content');
  });
});

describe('canWrite', () => {
  it('is false for a reader — the mode switch collapses to Read only', () => {
    expect(canWrite(holder('content:read', 'media:read'))).toBe(false);
  });

  it('is true for an author', () => {
    expect(canWrite(holder('content:write'))).toBe(true);
  });

  it('is true for someone who can only upload', () => {
    expect(canWrite(holder('media:write'))).toBe(true);
  });

  it('is false before permissions have loaded', () => {
    expect(canWrite(undefined)).toBe(false);
  });
});

describe('the writing tools', () => {
  beforeEach(() => vi.resetAllMocks());

  it('creates a draft under the collection the model named', async () => {
    vi.mocked(api.post).mockResolvedValue({ id: 'new' });
    await tool('create_content').run(
      {
        instanceId: 'inst-1',
        contentType: 'article',
        slug: 'hello-world',
        data: { title: 'Hello' },
      },
      noContext,
    );
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
    await tool('update_content').run({ id: 'item-1', data: { title: 'New' } }, noContext);
    expect(api.put).toHaveBeenCalledWith('/admin/content/item-1', {
      data: { title: 'New', body: 'Kept' },
    });
  });

  it('reports the before and after of the fields it changed', async () => {
    // The transcript's field diff is this, not a later comparison: by the time anyone opens
    // the card, the draft *is* the new one and the old values are gone.
    vi.mocked(api.get).mockResolvedValue({ draft: { title: 'Old', body: 'Kept' } });
    vi.mocked(api.put).mockResolvedValue({ versionNo: 2 });
    const result = await tool('update_content').run(
      { id: 'item-1', data: { title: 'New' } },
      noContext,
    );
    expect(JSON.parse(result).changed).toEqual({
      before: { title: 'Old' },
      after: { title: 'New' },
    });
  });

  it('survives an item that has no draft yet', async () => {
    vi.mocked(api.get).mockResolvedValue({ draft: null });
    vi.mocked(api.put).mockResolvedValue({});
    await tool('update_content').run({ id: 'item-1', data: { title: 'First' } }, noContext);
    expect(api.put).toHaveBeenCalledWith('/admin/content/item-1', { data: { title: 'First' } });
  });

  it('refuses a call with no id rather than requesting /content/undefined', async () => {
    await expect(tool('publish_content').run({}, noContext)).rejects.toThrow(/required/);
    expect(api.post).not.toHaveBeenCalled();
  });

  it('uploads a file the operator attached, by name', async () => {
    const file = new File(['x'], 'photo.jpg', { type: 'image/jpeg' });
    vi.mocked(api.upload).mockResolvedValue({ id: 'asset-1' });
    const uploaded = vi.fn();
    const result = await tool('upload_media').run(
      { fileName: 'photo.jpg', folderId: 'folder-1' },
      {
        attachments: [{ name: 'photo.jpg', size: 1, type: 'image/jpeg', file }],
        onUploaded: uploaded,
      },
    );
    expect(api.upload).toHaveBeenCalledWith('/admin/media', expect.any(FormData));
    expect(uploaded).toHaveBeenCalledWith('photo.jpg', 'asset-1');
    expect(JSON.parse(result).id).toBe('asset-1');
  });

  it('will not upload bytes it does not have, and says which files it does', async () => {
    // The model cannot produce a file. Naming what is attached is what lets it recover;
    // "not found" makes it guess the same wrong name again.
    await expect(
      tool('upload_media').run(
        { fileName: 'ghost.png' },
        { attachments: [], onUploaded: () => {} },
      ),
    ).rejects.toThrow(/nothing/);
    expect(api.upload).not.toHaveBeenCalled();
  });

  it('does not upload the same attachment twice', async () => {
    const file = new File(['x'], 'photo.jpg', { type: 'image/jpeg' });
    const result = await tool('upload_media').run(
      { fileName: 'photo.jpg' },
      {
        attachments: [{ name: 'photo.jpg', size: 1, type: 'image/jpeg', file, assetId: 'already' }],
        onUploaded: () => {},
      },
    );
    expect(api.upload).not.toHaveBeenCalled();
    expect(JSON.parse(result)).toEqual({ id: 'already', alreadyUploaded: true });
  });

  it("says what publishing will do, in the operator's terms", () => {
    expect(tool('publish_content').summarize!({ id: 'item-1' })).toMatch(/public/);
  });
});
