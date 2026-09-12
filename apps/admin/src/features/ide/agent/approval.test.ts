import { describe, expect, it } from 'vitest';
import type { ToolCall } from '../../agent/contracts';
import { countLines, previewOf, previewTitle } from './approval';

/**
 * What the approval card shows, proved without rendering it.
 *
 * <p>The card's whole claim is that a person can see the change before agreeing to it. That
 * claim lives in this derivation, not in the markup — a diff that reads the wrong argument name
 * renders beautifully and shows nothing, which is the failure worth testing for.</p>
 */

const FILES = {
  'src/App.tsx': 'import React from "react";\n\nexport default function App() {\n  return <main>Hello</main>;\n}\n',
  'src/old.ts': 'const a = 1;\nconst b = 2;\n',
};

const call = (name: string, input: Record<string, unknown>): ToolCall => ({ id: 't1', name, input });

describe('previewOf', () => {
  it('shows an edit as the text out and the text in', () => {
    const preview = previewOf(
      call('edit_file', { path: 'src/App.tsx', old_text: 'Hello', new_text: 'Get started' }),
      FILES,
    );
    expect(preview).toEqual({
      kind: 'edit',
      path: 'src/App.tsx',
      removed: ['Hello'],
      added: ['Get started'],
      atLine: 4,
    });
  });

  it('says where in the file the edit lands', () => {
    // "src/App.tsx" is a file; "src/App.tsx:4" is a place. The second is reviewable.
    const preview = previewOf(
      call('edit_file', { path: 'src/App.tsx', old_text: 'import React', new_text: 'import * as React' }),
      FILES,
    );
    expect(previewTitle(preview)).toBe('src/App.tsx:1');
  });

  it('does not invent a line for an anchor that is not in the file', () => {
    // A wrong anchor is a failure the tool will report; the card must not pretend it resolved.
    const preview = previewOf(
      call('edit_file', { path: 'src/App.tsx', old_text: 'nowhere', new_text: 'x' }),
      FILES,
    );
    expect(preview).toMatchObject({ atLine: undefined });
    expect(previewTitle(preview)).toBe('src/App.tsx');
  });

  it('reads the real lines a range replaces, not just the new text', () => {
    const preview = previewOf(
      call('replace_lines', { path: 'src/old.ts', start_line: 1, end_line: 2, text: 'const c = 3;' }),
      FILES,
    );
    expect(preview).toMatchObject({
      kind: 'edit',
      removed: ['const a = 1;', 'const b = 2;'],
      added: ['const c = 3;'],
      atLine: 1,
    });
  });

  it('treats an insert as pure addition at the line it lands on', () => {
    // `line` is the line inserted *before*, so it is already the destination.
    const preview = previewOf(
      call('insert_lines', { path: 'src/old.ts', line: 1, text: '// header' }),
      FILES,
    );
    expect(preview).toMatchObject({ kind: 'edit', removed: [], added: ['// header'], atLine: 1 });
  });

  it('shows the lines a delete actually removes', () => {
    const preview = previewOf(
      call('delete_lines', { path: 'src/old.ts', start_line: 2, end_line: 2 }),
      FILES,
    );
    expect(preview).toMatchObject({ kind: 'edit', removed: ['const b = 2;'], added: [] });
  });

  it('shows a new file as its contents', () => {
    const preview = previewOf(
      call('create_file', { path: 'src/New.tsx', content: 'export const x = 1;\n' }),
      FILES,
    );
    expect(preview).toEqual({ kind: 'create', path: 'src/New.tsx', added: ['export const x = 1;'] });
  });

  it('shows a deletion as the whole file being agreed away', () => {
    const preview = previewOf(call('delete_file', { path: 'src/old.ts' }), FILES);
    expect(preview).toMatchObject({ kind: 'delete', removed: ['const a = 1;', 'const b = 2;'] });
  });

  it('reads rename from `from` and `to`, the one tool with no path', () => {
    // Getting this wrong renders a card that says "Rename  to " and nobody can tell what it does.
    const preview = previewOf(call('rename_file', { from: 'src/old.ts', to: 'src/new.ts' }), FILES);
    expect(preview).toEqual({ kind: 'action', text: 'Rename src/old.ts to src/new.ts' });
  });

  it('falls back to the tool’s own summary when there is no file to diff', () => {
    const preview = previewOf(call('publish_content', { id: 'abc' }), FILES, {
      summarize: () => 'Publish 1 article',
    });
    expect(preview).toEqual({ kind: 'action', text: 'Publish 1 article' });
  });

  it('still says something for a tool that forgot to summarize', () => {
    expect(previewOf(call('mystery_tool', {}), FILES)).toEqual({
      kind: 'action',
      text: 'Run mystery_tool',
    });
  });

  it('does not count a file’s trailing newline as an added blank line', () => {
    const preview = previewOf(
      call('create_file', { path: 'a.ts', content: 'one\ntwo\n' }),
      FILES,
    );
    expect(preview).toMatchObject({ added: ['one', 'two'] });
  });
});

describe('countLines', () => {
  it('totals a batch the way the header reports it', () => {
    const previews = [
      previewOf(call('edit_file', { path: 'src/App.tsx', old_text: 'Hello', new_text: 'a\nb\nc' }), FILES),
      previewOf(call('create_file', { path: 'n.ts', content: 'x\ny\n' }), FILES),
      previewOf(call('delete_file', { path: 'src/old.ts' }), FILES),
    ];
    expect(countLines(previews)).toEqual({ added: 5, removed: 3 });
  });

  it('ignores actions, which change no lines', () => {
    const previews = [previewOf(call('publish_content', {}), FILES, { summarize: () => 'Publish' })];
    expect(countLines(previews)).toEqual({ added: 0, removed: 0 });
  });
});
