import { describe, expect, it } from 'vitest';
import type { MyPermissions } from '@dcms/core';
import { ASSISTANT_TOOLS, toolDefinitions, toolsFor } from './tools';

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

  it('offers everything to a SuperAdmin', () => {
    expect(toolsFor({ isSuperAdmin: true, permissions: [] })).toHaveLength(ASSISTANT_TOOLS.length);
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

  it('is read-only for now', () => {
    // A model that can publish or delete needs an approval flow showing exactly what will
    // change. Shipping write tools before that exists trades a real risk for a demo.
    for (const tool of ASSISTANT_TOOLS) {
      expect(tool.mutates, `${tool.name} mutates`).toBeFalsy();
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
