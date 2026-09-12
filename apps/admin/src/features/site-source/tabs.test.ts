import { beforeEach, describe, expect, it } from 'vitest';
import { useVfs } from './vfs';

/**
 * Pinned tabs.
 *
 * <p>A pin exists to survive churn, so the behaviour worth pinning down is what happens when
 * things are being closed around it — an agent run opens twenty files, and the reference the
 * author was working against has to still be there afterwards.</p>
 */

beforeEach(() => {
  useVfs.setState({
    files: { 'a.ts': '', 'b.ts': '', 'c.ts': '' },
    openTabs: ['a.ts', 'b.ts', 'c.ts'],
    pinnedTabs: [],
    activePath: 'c.ts',
  });
});

describe('togglePin', () => {
  it('pins and unpins', () => {
    useVfs.getState().togglePin('a.ts');
    expect(useVfs.getState().pinnedTabs).toEqual(['a.ts']);
    useVfs.getState().togglePin('a.ts');
    expect(useVfs.getState().pinnedTabs).toEqual([]);
  });

  it('does not open or close anything', () => {
    useVfs.getState().togglePin('a.ts');
    expect(useVfs.getState().openTabs).toEqual(['a.ts', 'b.ts', 'c.ts']);
  });
});

describe('closeOthers', () => {
  it('keeps the named tab and every pinned one', () => {
    useVfs.getState().togglePin('a.ts');
    useVfs.getState().closeOthers('c.ts');
    expect(useVfs.getState().openTabs).toEqual(['a.ts', 'c.ts']);
  });

  it('keeps only the pins when nothing is named', () => {
    useVfs.getState().togglePin('b.ts');
    useVfs.getState().closeOthers(null);
    expect(useVfs.getState().openTabs).toEqual(['b.ts']);
  });

  it('moves the active tab when it was closed', () => {
    useVfs.setState({ activePath: 'b.ts' });
    useVfs.getState().closeOthers('a.ts');
    expect(useVfs.getState().activePath).toBe('a.ts');
  });

  it('leaves the active tab alone when it survived', () => {
    useVfs.getState().closeOthers('c.ts');
    expect(useVfs.getState().activePath).toBe('c.ts');
  });

  it('can close everything', () => {
    useVfs.getState().closeOthers(null);
    expect(useVfs.getState().openTabs).toEqual([]);
    expect(useVfs.getState().activePath).toBeNull();
  });
});

describe('closeTab', () => {
  it('drops the pin with the tab', () => {
    // A pin that outlived its tab would resurrect it on the next open — a tab reappearing for
    // reasons the author cannot see.
    useVfs.getState().togglePin('a.ts');
    useVfs.getState().closeTab('a.ts');
    expect(useVfs.getState().pinnedTabs).toEqual([]);
  });

  it('leaves other pins intact', () => {
    useVfs.getState().togglePin('a.ts');
    useVfs.getState().closeTab('b.ts');
    expect(useVfs.getState().pinnedTabs).toEqual(['a.ts']);
  });
});
