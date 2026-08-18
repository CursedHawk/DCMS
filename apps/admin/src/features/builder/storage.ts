import { pageBodyOf } from '@dcms/gjs-blocks';
import { GLOBAL_CSS, pageCssPath, renderThemeCss } from '@dcms/gjs-schema';
import type { Editor } from 'grapesjs';
import { SOURCE_PROP, applyThemeCss, cssHandoff, htmlHandoff } from './grapes';
import type { Project, ProjectPage } from './project';
import { parseClient } from './workers/parseClient';

/**
 * The bridge between the GrapesJS canvas and the project's files.
 *
 * Only one page is loaded into the editor at a time. GrapesJS's CssComposer is
 * global, so holding every page's rules at once would let one page's `.hero`
 * style another page's — the canvas would then disagree with the published site,
 * where each page links only `theme.css`, `global.css` and its own stylesheet.
 * Loading one page keeps the canvas cascade identical to the real one.
 *
 * Every rule is tagged with the file it came from (`SOURCE_PROP`), so capturing
 * writes each rule back where it belongs and a shared class edited from any page
 * keeps living in `global.css` instead of being copied into each page.
 */

export interface CapturedPage {
  html: string;
  css: string;
  globalCss: string;
}

/** Load a page's markup and rules into the editor. */
export async function loadPageIntoEditor(
  editor: Editor,
  project: Project,
  page: ProjectPage,
): Promise<void> {
  const pageCss = pageCssPath(page.entry.slug);

  // Parse everything in the worker first, then hand the results to the
  // synchronous GrapesJS parser hooks (see ParseHandoff in grapes.ts).
  const [parsedHtml, globalRules, pageRules] = await Promise.all([
    parseClient.parseHtml(`page:${page.entry.slug}`, page.html),
    parseClient.parseCss(GLOBAL_CSS, project.globalCss),
    parseClient.parseCss(pageCss, page.css),
  ]);

  // Replacing the whole page is not an undoable edit — it is the starting point.
  editor.UndoManager.stop();
  try {
    editor.Css.clear();

    if (project.globalCss.trim()) {
      cssHandoff.preload(project.globalCss, globalRules);
      editor.Css.addCollection(project.globalCss, {}, { [SOURCE_PROP]: GLOBAL_CSS });
    }
    if (page.css.trim()) {
      cssHandoff.preload(page.css, pageRules);
      editor.Css.addCollection(page.css, {}, { [SOURCE_PROP]: pageCss });
    }

    htmlHandoff.preload(page.html, parsedHtml.nodes);
    editor.setComponents(page.html);
  } finally {
    editor.UndoManager.start();
  }

  editor.UndoManager.clear();
  editor.select(undefined);
  applyThemeCss(editor, renderThemeCss(project.manifest.theme));
}

/**
 * Serialize the canvas back to page markup and stylesheets.
 *
 * Formatting runs in the worker: the canvas serializes to one long line, and a
 * one-line `pages/home.html` would make every git diff unreadable — which is the
 * whole reason this format stores HTML instead of a JSON blob.
 */
export async function capturePage(editor: Editor, slug: string): Promise<CapturedPage> {
  const pageCss = pageCssPath(slug);
  const html = editor.getHtml({ cleanId: true });

  const globalParts: string[] = [];
  const pageParts: string[] = [];
  for (const rule of editor.Css.getAll().models) {
    const css = rule.toCSS();
    if (!css) continue;
    // A rule created while this page was open (by the Style Manager) has no
    // source yet; it belongs to the page being edited, which is the least
    // surprising home for "I styled this here".
    const source = (rule.get(SOURCE_PROP) as string | undefined) ?? pageCss;
    (source === GLOBAL_CSS ? globalParts : pageParts).push(css);
  }

  const formatted = await parseClient.format(`capture:${slug}`, {
    // GrapesJS wraps the page in <body>; the repo stores page bodies, and
    // re-loading an unstripped result would nest another wrapper on every save.
    html: pageBodyOf(html),
    css: pageParts.join('\n'),
  });
  const formattedGlobal = await parseClient.format(GLOBAL_CSS, { css: globalParts.join('\n') });

  return {
    html: formatted.html ?? '',
    css: formatted.css ?? '',
    globalCss: formattedGlobal.css ?? '',
  };
}

/** Fold a captured page back into the project model. */
export function applyCapture(project: Project, slug: string, captured: CapturedPage): Project {
  return {
    ...project,
    globalCss: captured.globalCss,
    pages: project.pages.map((p) =>
      p.entry.slug === slug ? { ...p, html: captured.html, css: captured.css } : p,
    ),
  };
}
