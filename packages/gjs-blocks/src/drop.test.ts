// @vitest-environment jsdom
import type { ParsedNode } from '@dcms/gjs-parse';
import grapesjs, { type Editor } from 'grapesjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { parseCssSync, parseHtmlSync } from './parsers';
import { dcmsCore } from './plugins/core';
import { defaultSnippet } from './register';
import { BUILTIN_SPECS } from './specs';

/**
 * Dropping a block onto the canvas.
 *
 * The round-trip suite always feeds GrapesJS content it has parsed itself and
 * put in the memo first. Real drag and drop never does that: GrapesJS takes the
 * block's `content` string and parses it through the configured parser on its
 * own. With a memo-only parser that returned `[]` on a miss, every drop produced
 * zero components — silently, with nothing in the console — and no test noticed,
 * because no test exercised an *unpreloaded* input.
 *
 * These tests therefore deliberately do NOT preload. They are the difference
 * between "the parser works" and "the builder works".
 */

/** The parser wiring `createEditor` uses in production. */
function productionParser(memo: Map<string, ParsedNode[]>) {
  return {
    parsersCode: { worker: (input: string) => memo.get(input) ?? parseHtmlSync(input) },
    parserCode: 'worker',
    parserCss: (input: string) => parseCssSync(input),
    optionsHtml: { allowScripts: false, allowUnsafeAttr: false },
  };
}

let editor: Editor;
const memo = new Map<string, ParsedNode[]>();

function makeEditor(parser: object): Editor {
  const container = document.createElement('div');
  document.body.appendChild(container);
  return grapesjs.init({
    container,
    headless: true,
    storageManager: false,
    panels: { defaults: [] },
    parser: parser as never,
    plugins: [(e) => dcmsCore(e, { enabledPluginIds: ['forms'] })],
  });
}

beforeEach(() => {
  memo.clear();
  editor = makeEditor(productionParser(memo));
});

afterEach(() => editor.destroy());

/** Every component type currently on the canvas. */
function types(): string[] {
  type Node = NonNullable<ReturnType<Editor['getWrapper']>>;
  const out: string[] = [];
  const visit = (c: Node) => {
    out.push(String(c.get('type') ?? ''));
    c.components().forEach(visit as never);
  };
  const wrapper = editor.getWrapper();
  if (wrapper) visit(wrapper);
  return out;
}

describe('appending block content the way a drop does', () => {
  it('adds components for markup that was never preloaded', () => {
    const wrapper = editor.getWrapper()!;
    wrapper.append('<section class="dcms-section dcms-hero"><h1>Hi</h1></section>');
    expect(wrapper.components().length).toBe(1);
  });

  it('would add nothing with a memo-only parser — the original bug', () => {
    const bare = makeEditor({
      parsersCode: { worker: (input: string) => memo.get(input) ?? [] },
      parserCode: 'worker',
      parserCss: () => [],
    });
    bare.getWrapper()!.append('<section class="dcms-section dcms-hero"><h1>Hi</h1></section>');
    expect(bare.getWrapper()!.components().length).toBe(0);
    bare.destroy();
  });

  it.each(BUILTIN_SPECS.map((s) => [s.type, s] as const))(
    'drops %s as a real component',
    (_type, spec) => {
      const wrapper = editor.getWrapper()!;
      wrapper.append(spec.snippet ?? defaultSnippet(spec));
      expect(wrapper.components().length, `${spec.type} dropped nothing`).toBeGreaterThan(0);
      expect(types(), `${spec.type} was not identified`).toContain(spec.type);
    },
  );

  it('drops a block into an existing container rather than replacing it', () => {
    const wrapper = editor.getWrapper()!;
    wrapper.append('<section class="dcms-section dcms-hero"></section>');
    const hero = wrapper.components().at(0);
    hero.append('<p class="dcms-hero-text">Added</p>');

    expect(hero.components().length).toBe(1);
    expect(editor.getHtml({ cleanId: true })).toContain('Added');
  });

  it('parses a dropped block identically to the same markup preloaded', () => {
    const snippet = '<section class="dcms-section dcms-hero"><h1 class="dcms-hero-title">Hi</h1></section>';

    editor.getWrapper()!.append(snippet);
    const viaFallback = editor.getHtml({ cleanId: true });

    // Same input, this time through the worker memo, as a page load would.
    const preloaded = makeEditor(productionParser(new Map([[snippet, parseHtmlSync(snippet)]])));
    preloaded.getWrapper()!.append(snippet);
    const viaMemo = preloaded.getHtml({ cleanId: true });
    preloaded.destroy();

    // If these diverge, a block behaves differently depending on how it arrived.
    expect(viaFallback).toBe(viaMemo);
  });
});

describe('css parsed outside the worker', () => {
  it('parses a stylesheet GrapesJS hands to the parser itself', () => {
    editor.Css.addRules('.dropped { color: red }');
    // Asserted on the composer, not on `getCss()`: GrapesJS omits rules that
    // match no component from its output, so an empty `getCss()` would not tell
    // us whether the rule failed to parse or merely went unused.
    expect(editor.Css.getRule('.dropped')).toBeTruthy();
  });

  it('emits those rules once something on the page uses them', () => {
    editor.getWrapper()!.append('<section class="dcms-section dropped"></section>');
    editor.Css.addRules('.dropped { color: red }');
    expect(editor.getCss()).toContain('.dropped');
  });
});
