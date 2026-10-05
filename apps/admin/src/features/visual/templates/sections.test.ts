import { builtinRegistry, checkVisualSite, type Node } from '@dcms/site-runtime';
import { renderToStaticMarkup } from 'react-dom/server';
import { createElement } from 'react';
import { RenderModeContext, RenderNode } from '@dcms/site-runtime';
import { describe, expect, it } from 'vitest';
import { nodeFromStarter } from '../canvas/tree';
import { PAGE_TEMPLATES, pageBody } from './pages';
import { SECTION_TEMPLATES } from './sections';

const json = (v: unknown) => `${JSON.stringify(v)}\n`;

describe('the section library', () => {
  it('has unique ids and says what each template is for', () => {
    expect(new Set(SECTION_TEMPLATES.map((t) => t.id)).size).toBe(SECTION_TEMPLATES.length);
    for (const t of SECTION_TEMPLATES) {
      expect(t.description.length, t.id).toBeGreaterThan(20);
      expect(t.keywords.length, t.id).toBeGreaterThanOrEqual(2);
    }
  });

  it('inserts as a valid page: every prop allowed, every child where it may go, ids unique', () => {
    for (const t of SECTION_TEMPLATES) {
      const tree = nodeFromStarter(t.tree, builtinRegistry);
      const problems = checkVisualSite({
        'dcms/app.json': json({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }),
        'dcms/pages/home.json': json({ schemaVersion: 1, id: 'home', title: 'Home', root: { id: 'r', type: 'dcms.page', slots: { default: [tree] } } }),
      });
      // What only the author can fill in (which plugin a form posts to, which content a list
      // shows) is reported as required; nothing else may be wrong.
      expect(problems.filter((p) => !p.message.endsWith(' is required.')), t.id).toEqual([]);
    }
  });

  it('renders on the canvas and on the site without throwing', () => {
    for (const t of SECTION_TEMPLATES) {
      const tree: Node = nodeFromStarter(t.tree, builtinRegistry);
      for (const mode of ['edit', 'live'] as const) {
        expect(() => renderToStaticMarkup(createElement(RenderModeContext.Provider, { value: mode }, createElement(RenderNode, { node: tree, registry: builtinRegistry }))), `${t.id} ${mode}`).not.toThrow();
      }
    }
  });
});

describe('page templates', () => {
  it('are made of sections that exist', () => {
    const ids = new Set(SECTION_TEMPLATES.map((t) => t.id));
    for (const p of PAGE_TEMPLATES) for (const s of p.sections) expect(ids.has(s), `${p.id}: ${s}`).toBe(true);
    expect(pageBody(PAGE_TEMPLATES.find((p) => p.id === 'about')!, builtinRegistry).map((n) => n.type)).toEqual(Array(5).fill('dcms.section'));
  });
});
