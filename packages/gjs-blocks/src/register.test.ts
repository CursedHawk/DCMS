// @vitest-environment jsdom
import { handleRequest, type ParsedCssRule, type ParsedNode } from '@dcms/gjs-parse';
import { identityClassOf } from '@dcms/gjs-schema';
import grapesjs, { type Editor } from 'grapesjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { dcmsCore } from './plugins/core';
import { defaultSnippet } from './register';
import { pageBodyOf } from './serialize';
import { BUILTIN_SPECS } from './specs';

/**
 * Integration proof for the registration path.
 *
 * The matcher tests in roundtrip.test.ts check our own logic; this checks that
 * GrapesJS actually *uses* it — that `addType` took, that the `isParsedNode`
 * static reached the constructor GrapesJS reads it from, and that a page of
 * markup parsed through the worker contract comes back as the right component
 * types. Those are three separate places the wiring could be right in isolation
 * and wrong together.
 */

// Stand-ins for the worker handoff: the parsers GrapesJS calls must be
// synchronous, so the editor is given the same memo-table shape the builder uses.
const htmlMemo = new Map<string, ParsedNode[]>();
const cssMemo = new Map<string, ParsedCssRule[]>();

function preparseHtml(input: string): string {
  const res = handleRequest({ kind: 'parse-html', id: 1, key: 'test', input });
  if (res.kind !== 'parse-html') throw new Error('parse failed');
  htmlMemo.set(input, res.result.nodes);
  return input;
}

function preparseCss(input: string): string {
  const res = handleRequest({ kind: 'parse-css', id: 1, key: 'test', input });
  if (res.kind !== 'parse-css') throw new Error('parse failed');
  cssMemo.set(input, res.result);
  return input;
}

let editor: Editor;

beforeEach(() => {
  const container = document.createElement('div');
  document.body.appendChild(container);
  editor = grapesjs.init({
    container,
    headless: true,
    storageManager: false,
    panels: { defaults: [] },
    parser: {
      parsersCode: { worker: (input) => htmlMemo.get(input) ?? [] },
      parserCode: 'worker',
      parserCss: (input) => cssMemo.get(input) ?? [],
      optionsHtml: { allowScripts: false, allowUnsafeAttr: false },
    },
    plugins: [(e) => dcmsCore(e, { enabledPluginIds: ['forms'] })],
  });
});

afterEach(() => {
  editor.destroy();
  htmlMemo.clear();
  cssMemo.clear();
});

/** Walk every component in the canvas. */
function allComponents(): { type: string; classes: string[] }[] {
  type Node = NonNullable<ReturnType<Editor['getWrapper']>>;
  const out: { type: string; classes: string[] }[] = [];
  const visit = (component: Node) => {
    out.push({ type: String(component.get('type') ?? ''), classes: component.getClasses() });
    component.components().forEach(visit as never);
  };
  const wrapper = editor.getWrapper();
  if (wrapper) visit(wrapper);
  return out;
}

describe('dcmsCore registration', () => {
  it('registers a component type for every spec', () => {
    for (const spec of BUILTIN_SPECS) {
      // Forms blocks are gated on the plugin, which this editor enables.
      expect(editor.DomComponents.getType(spec.type), spec.type).toBeTruthy();
    }
  });

  it('attaches isParsedNode where GrapesJS reads it', () => {
    for (const spec of BUILTIN_SPECS) {
      const model = editor.DomComponents.getType(spec.type)!.model as unknown as {
        isParsedNode?: unknown;
        isComponent?: unknown;
      };
      expect(typeof model.isParsedNode, `${spec.type}.isParsedNode`).toBe('function');
      expect(typeof model.isComponent, `${spec.type}.isComponent`).toBe('function');
    }
  });

  it('adds a palette block for every spec', () => {
    for (const spec of BUILTIN_SPECS) {
      expect(editor.BlockManager.get(spec.type), spec.type).toBeTruthy();
    }
  });

  it('gates blocks whose plugin is not enabled', () => {
    const container = document.createElement('div');
    document.body.appendChild(container);
    const bare = grapesjs.init({ container, headless: true, storageManager: false, plugins: [(e) => dcmsCore(e)] });
    // Form and NewsletterSignup both require the forms plugin.
    expect(bare.BlockManager.get('Form')).toBeFalsy();
    expect(bare.BlockManager.get('Hero')).toBeTruthy();
    bare.destroy();
  });
});

describe('parsing a page back into components', () => {
  it.each(BUILTIN_SPECS.map((s) => [s.type, s] as const))('re-identifies %s', (_type, spec) => {
    const html = spec.snippet ?? defaultSnippet(spec);
    editor.setComponents(preparseHtml(html));

    const found = allComponents().some((c) => c.type === spec.type);
    expect(found, `${spec.type} was not re-identified after parsing`).toBe(true);
  });

  it('keeps types across a full page of stacked sections', () => {
    const sections = BUILTIN_SPECS.filter((s) => s.category === 'section');
    const page = sections.map((s) => s.snippet ?? defaultSnippet(s)).join('\n');
    editor.setComponents(preparseHtml(page));

    const types = new Set(allComponents().map((c) => c.type));
    for (const spec of sections) {
      expect(types.has(spec.type), `${spec.type} lost its type inside a full page`).toBe(true);
    }
  });

  it('serializes back to markup that still carries every identity class', () => {
    const sections = BUILTIN_SPECS.filter((s) => s.category === 'section');
    const page = sections.map((s) => s.snippet ?? defaultSnippet(s)).join('\n');
    editor.setComponents(preparseHtml(page));

    const html = editor.getHtml({ cleanId: true });
    for (const spec of sections) {
      expect(html, `${spec.type} lost its identity class on serialize`).toContain(identityClassOf(spec));
    }
  });

  it('round-trips a page through the real save/load cycle without drift', () => {
    const page = BUILTIN_SPECS.filter((s) => s.category === 'section')
      .slice(0, 5)
      .map((s) => s.snippet ?? defaultSnippet(s))
      .join('\n');

    // This mirrors the storage bridge exactly: capture strips the wrapper before
    // writing the file, and load feeds that stripped body back in.
    editor.setComponents(preparseHtml(page));
    const first = pageBodyOf(editor.getHtml({ cleanId: true }));

    editor.setComponents(preparseHtml(first));
    const second = pageBodyOf(editor.getHtml({ cleanId: true }));

    // The first pass may normalize (attribute order, quoting); the second must
    // not — otherwise every autosave would rewrite the file and churn git.
    expect(second).toBe(first);
  });

  it('does not nest a second wrapper when a captured page is reloaded', () => {
    editor.setComponents(preparseHtml('<section class="dcms-hero dcms-section"><p>x</p></section>'));
    const captured = pageBodyOf(editor.getHtml({ cleanId: true }));

    expect(captured.startsWith('<body')).toBe(false);
    editor.setComponents(preparseHtml(captured));
    // Without the strip this would be `<body><body>…`, and every save would add
    // another level.
    expect(pageBodyOf(editor.getHtml({ cleanId: true })).startsWith('<body')).toBe(false);
  });
});

describe('pageBodyOf', () => {
  it('unwraps a GrapesJS body, with or without attributes', () => {
    expect(pageBodyOf('<body><p>x</p></body>')).toBe('<p>x</p>');
    expect(pageBodyOf('<body id="i3" class="c"><p>x</p></body>')).toBe('<p>x</p>');
  });

  it('leaves a bare page body alone', () => {
    expect(pageBodyOf('<section>x</section>')).toBe('<section>x</section>');
  });

  it('does not unwrap a body nested inside other markup', () => {
    // Only a whole-string wrapper is GrapesJS's; anything else is author markup.
    const html = '<div><body>x</body></div>';
    expect(pageBodyOf(html)).toBe(html);
  });
});

describe('css handling', () => {
  it('loads rules through the worker parser contract', () => {
    const css = '.dcms-hero { padding: 4rem; } @media (max-width: 640px) { .dcms-hero { padding: 2rem; } }';
    editor.Css.addCollection(preparseCss(css), {}, { dcmsSource: 'styles/global.css' });

    const rules = editor.Css.getAll().models;
    expect(rules.length).toBeGreaterThanOrEqual(2);
    expect(rules.every((r) => r.get('dcmsSource' as never) === 'styles/global.css')).toBe(true);
    expect(rules.some((r) => r.getAtRule().includes('max-width'))).toBe(true);
  });

  it('keeps each rule attributable to the file it came from', () => {
    editor.Css.addCollection(preparseCss('.a { color: red }'), {}, { dcmsSource: 'styles/global.css' });
    editor.Css.addCollection(preparseCss('.b { color: blue }'), {}, { dcmsSource: 'styles/pages/home.css' });

    const bySource = new Map(
      editor.Css.getAll().models.map((r) => [r.selectorsToString(), r.get('dcmsSource' as never)]),
    );
    expect(bySource.get('.a')).toBe('styles/global.css');
    expect(bySource.get('.b')).toBe('styles/pages/home.css');
  });
});
