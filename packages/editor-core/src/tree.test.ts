import { describe, expect, it } from 'vitest';
import type { ComponentNode } from './schema';
import { findNode, findParent, insertNode, moveNode, removeNode, updateProps } from './tree';

const node = (id: string, type = 'Section', children: ComponentNode[] = []): ComponentNode => ({
  id,
  type,
  props: {},
  children,
});

const tree = (): ComponentNode =>
  node('root', 'Section', [node('a', 'Text'), node('b', 'Section', [node('b1', 'Text')])]);

describe('tree ops', () => {
  it('finds nodes and parents', () => {
    const t = tree();
    expect(findNode(t, 'b1')?.id).toBe('b1');
    expect(findNode(t, 'missing')).toBeUndefined();
    expect(findParent(t, 'b1')?.id).toBe('b');
    expect(findParent(t, 'root')).toBeUndefined();
  });

  it('inserts without mutating the input', () => {
    const t = tree();
    const next = insertNode(t, 'root', node('c', 'Image'), 1);
    expect(next.children?.map((c) => c.id)).toEqual(['a', 'c', 'b']);
    expect(t.children?.map((c) => c.id)).toEqual(['a', 'b']); // original unchanged
  });

  it('removes a node', () => {
    const next = removeNode(tree(), 'b1');
    expect(findNode(next, 'b1')).toBeUndefined();
    expect(findNode(next, 'b')?.children).toHaveLength(0);
  });

  it('updates props by merging', () => {
    const next = updateProps(tree(), 'a', { text: 'hi' });
    expect(findNode(next, 'a')?.props).toEqual({ text: 'hi' });
  });

  it('moves a node into another parent', () => {
    const next = moveNode(tree(), 'a', 'b', 0);
    expect(findNode(next, 'root')?.children?.map((c) => c.id)).toEqual(['b']);
    expect(findNode(next, 'b')?.children?.map((c) => c.id)).toEqual(['a', 'b1']);
  });

  it('refuses to move a node into its own subtree (no cycle)', () => {
    const t = tree();
    const next = moveNode(t, 'b', 'b1');
    expect(next).toBe(t); // unchanged
  });
});
