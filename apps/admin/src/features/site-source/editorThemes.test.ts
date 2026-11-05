import { describe, expect, it } from 'vitest';
import type * as Monaco from 'monaco-editor';
import { defineEditorThemes, EDITOR_THEMES, resolveEditorTheme } from './editorThemes';
import { encodeTokens, fromTypeScript, LEGEND, TOKEN } from './semanticTokens';

function themes() {
  const defined = new Map<string, Monaco.editor.IStandaloneThemeData>();
  defineEditorThemes({ editor: { defineTheme: (name: string, data: Monaco.editor.IStandaloneThemeData) => defined.set(name, data) } } as unknown as typeof Monaco);
  return defined;
}

describe('editor themes', () => {
  it('colour every semantic token type, so none is painted the plain foreground', () => {
    const defined = themes();
    expect(defined.size).toBe(EDITOR_THEMES.length + 2);
    for (const [name, data] of defined) {
      for (const type of LEGEND.tokenTypes) {
        expect(data.rules.find((r) => r.token === type)?.foreground, `${name}: ${type}`).toMatch(/^[0-9A-F]{6}$/i);
      }
    }
  });

  it('tells control flow from other keywords, in C# and TypeScript alike', () => {
    const dark = themes().get('dcms-dark-plus')!;
    expect(dark.rules.find((r) => r.token === 'keyword')?.foreground).toBe('569CD6');
    expect(dark.rules.find((r) => r.token === 'keyword.return')?.foreground).toBe('C586C0');
    expect(dark.rules.find((r) => r.token === 'keyword.string.ts')?.foreground).toBe('4EC9B0');
  });

  it('follows the console unless a theme was picked', () => {
    expect(resolveEditorTheme('auto', 'dark')).toBe('dcms-console-dark');
    expect(resolveEditorTheme('gone', 'light')).toBe('dcms-console-light');
    expect(resolveEditorTheme('monokai', 'light')).toBe('dcms-monokai');
  });
});

describe('semantic tokens', () => {
  it("maps TypeScript's classifications into the legend, keeping readonly", () => {
    // A readonly variable (type 7, modifiers declaration|readonly) and a method (type 11).
    expect(fromTypeScript([0, 3, (8 << 8) | 0b1001, 10, 4, 12 << 8])).toEqual([0, 3, TOKEN.variable, 1, 10, 4, TOKEN.method, 0]);
  });

  it('encodes spans relative to the one before, line by line', () => {
    const text = 'const a = 1;\nfoo(a);';
    const model = {
      getPositionAt(offset: number) {
        const before = text.slice(0, offset).split('\n');
        return { lineNumber: before.length, column: before[before.length - 1].length + 1 };
      },
    } as unknown as Monaco.editor.ITextModel;
    const { data } = encodeTokens(model, [6, 1, TOKEN.variable, 1, 13, 3, TOKEN.function, 0, 17, 1, TOKEN.variable, 1]);
    expect([...data]).toEqual([0, 6, 1, TOKEN.variable, 1, 1, 0, 3, TOKEN.function, 0, 0, 4, 1, TOKEN.variable, 1]);
  });
});
