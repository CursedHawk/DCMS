import { describe, expect, it } from 'vitest';
import type { ToolSpec } from './contracts';
import { DEFAULT_SCOPE, isTenantTool, storedScope, toolsFor } from './scope';

const spec = (name: string, permission?: string): ToolSpec<unknown> => ({
  name,
  description: name,
  input_schema: {},
  permission,
  run: async () => ({ content: '' }),
});

const REGISTRY = [
  spec('read_file'),
  spec('edit_file'),
  spec('check_build'),
  spec('preview_request'),
  spec('list_content'),
  spec('publish_content', 'content:publish'),
];

const names = (tools: ToolSpec<unknown>[]) => tools.map((t) => t.name);

const ALL_PERMS = { permissions: ['content:publish'], superadmin: false } as never;
const NO_PERMS = { permissions: [], superadmin: false } as never;

describe('classification', () => {
  it('recognises tenant tools', () => {
    expect(isTenantTool('list_content')).toBe(true);
    expect(isTenantTool('publish_content')).toBe(true);
    expect(isTenantTool('read_file')).toBe(false);
  });

  it('counts calling the site API as reaching the workspace', () => {
    // A preview writes the environment's live data (ADR 0026), so it is no longer harmless.
    expect(isTenantTool('preview_request')).toBe(true);
  });
});

describe('toolsFor', () => {
  it('offers only site tools in the default scope', () => {
    expect(names(toolsFor(REGISTRY, 'site', ALL_PERMS))).toEqual([
      'read_file',
      'edit_file',
      'check_build',
    ]);
  });

  it('adds tenant tools when the scope widens', () => {
    const widened = names(toolsFor(REGISTRY, 'site+tenant', ALL_PERMS));
    expect(widened).toContain('list_content');
    expect(widened).toContain('preview_request');
  });

  it('removes a tool the caller has no permission for, rather than letting it fail', () => {
    // A model told a tool exists will reach for it, and an assistant repeatedly announcing it
    // cannot do what it just offered reads as broken rather than careful.
    expect(names(toolsFor(REGISTRY, 'site+tenant', NO_PERMS))).not.toContain('publish_content');
  });

  it('keeps a permitted tool', () => {
    expect(names(toolsFor(REGISTRY, 'site+tenant', ALL_PERMS))).toContain('publish_content');
  });

  it('never gates site tools on scope', () => {
    for (const s of ['site', 'site+tenant'] as const) {
      expect(names(toolsFor(REGISTRY, s, ALL_PERMS))).toContain('edit_file');
    }
  });
});

describe('persistence', () => {
  it('always starts at the narrowest scope', () => {
    // "The agent may edit your live content" is a decision for the person sitting there, not
    // one restored from a fortnight ago because the browser still had the key.
    expect(storedScope()).toBe('site');
    expect(DEFAULT_SCOPE).toBe('site');
  });
});
