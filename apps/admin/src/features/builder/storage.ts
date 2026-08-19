import { pageBodyOf } from '@dcms/gjs-blocks';
import { GLOBAL_CSS, pageCssPath, renderThemeCss } from '@dcms/gjs-schema';
import type { Editor } from 'grapesjs';
import { SOURCE_PROP, applyThemeCss, cssHandoff, htmlHandoff } from './grapes';
import type { Project } from './project';
import type { CanvasTarget } from './store';
import { parseClient } from './workers/parseClient';

/**
 * The bridge between the GrapesJS canvas and the project's files.
 *
 * Only one document is loaded into the editor at a time. GrapesJS's CssComposer
 * is global, so holding every page's rules at once would let one page's `.hero`
 * style another page's — the canvas would then disagree with the published site,
 * where each page links only `theme.css`, `global.css` and its own stylesheet.
 * Loading one document keeps the canvas cascade identical to the real one.
 *
 * Every rule is tagged with the file it came from (`SOURCE_PROP`), so capturing
 * writes each rule back where it belongs and a shared class edited from any page
 * keeps living in `global.css` instead of being copied into each page.
 *
 * A shared region is loaded through the same path as a page, differing only in
 * having no stylesheet of its own — it appears on every page that uses it, so
 * its rules belong in `global.css` and nowhere else.
 */

export interface CapturedDoc {
  html: string;
  /** The target's own stylesheet; always empty for a region. */
  css: string;
  globalCss: string;
}

/** The document a target holds, or null if the target is not in the project. */
export function docFor(
  project: Project,
  target: CanvasTarget,
): { html: string; css: string; cssPath: string | null } | null {
  if (target.kind === 'region') {
    const region = project.regions.find((r) => r.entry.slug === target.slug);
    return region ? { html: region.html, css: '', cssPath: null } : null;
  }
  if (target.kind === 'component') {
    // The "document" is the definition's template. It is markup like any other,
    // which is what lets the component builder be this canvas rather than a
    // second editor with its own idea of what a block is.
    const definition = project.components.find((c) => c.name === target.slug);
    return definition ? { html: definition.template, css: '', cssPath: null } : null;
  }
  const page = project.pages.find((p) => p.entry.slug === target.slug);
  return page ? { html: page.html, css: page.css, cssPath: pageCssPath(target.slug) } : null;
}

/** Load a target's markup and rules into the editor. */
export async function loadTargetIntoEditor(
  editor: Editor,
  project: Project,
  target: CanvasTarget,
): Promise<void> {
  const doc = docFor(project, target);
  if (!doc) return;

  // Parse everything in the worker first, then hand the results to the
  // synchronous GrapesJS parser hooks (see ParseHandoff in grapes.ts).
  const [parsedHtml, globalRules, ownRules] = await Promise.all([
    parseClient.parseHtml(`${target.kind}:${target.slug}`, doc.html),
    parseClient.parseCss(GLOBAL_CSS, project.globalCss),
    doc.cssPath ? parseClient.parseCss(doc.cssPath, doc.css) : Promise.resolve([]),
  ]);

  // Replacing the whole document is not an undoable edit — it is the starting point.
  editor.UndoManager.stop();
  try {
    editor.Css.clear();

    if (project.globalCss.trim()) {
      cssHandoff.preload(project.globalCss, globalRules);
      editor.Css.addCollection(project.globalCss, {}, { [SOURCE_PROP]: GLOBAL_CSS });
    }
    if (doc.cssPath && doc.css.trim()) {
      cssHandoff.preload(doc.css, ownRules);
      editor.Css.addCollection(doc.css, {}, { [SOURCE_PROP]: doc.cssPath });
    }

    htmlHandoff.preload(doc.html, parsedHtml.nodes);
    editor.setComponents(doc.html);
  } finally {
    editor.UndoManager.start();
  }

  editor.UndoManager.clear();
  editor.select(undefined);
  applyThemeCss(editor, renderThemeCss(project.manifest.theme));
}

/**
 * Serialize the canvas back to markup and stylesheets.
 *
 * Formatting runs in the worker: the canvas serializes to one long line, and a
 * one-line `pages/home.html` would make every git diff unreadable — which is the
 * whole reason this format stores HTML instead of a JSON blob.
 */
export async function captureTarget(editor: Editor, target: CanvasTarget): Promise<CapturedDoc> {
  // Only a page has a stylesheet of its own; a rule authored while editing a
  // region or a component has to land in `global.css` — the alternative is
  // losing it on the next load.
  const ownCss = target.kind === 'page' ? pageCssPath(target.slug) : null;
  const html = editor.getHtml({ cleanId: true });

  const globalParts: string[] = [];
  const ownParts: string[] = [];
  for (const rule of editor.Css.getAll().models) {
    const css = rule.toCSS();
    if (!css) continue;
    // A rule created while this document was open (by the Style Manager) has no
    // source yet; it belongs to the document being edited, which is the least
    // surprising home for "I styled this here".
    const source = (rule.get(SOURCE_PROP) as string | undefined) ?? ownCss ?? GLOBAL_CSS;
    (source === GLOBAL_CSS || !ownCss ? globalParts : ownParts).push(css);
  }

  const formatted = await parseClient.format(`capture:${target.kind}:${target.slug}`, {
    // GrapesJS wraps the document in <body>; the repo stores bodies, and
    // re-loading an unstripped result would nest another wrapper on every save.
    html: pageBodyOf(html),
    css: ownParts.join('\n'),
  });
  const formattedGlobal = await parseClient.format(GLOBAL_CSS, { css: globalParts.join('\n') });

  return {
    html: formatted.html ?? '',
    css: formatted.css ?? '',
    globalCss: formattedGlobal.css ?? '',
  };
}

/** Fold a captured document back into the project model. */
export function applyCapture(
  project: Project,
  target: CanvasTarget,
  captured: CapturedDoc,
): Project {
  if (target.kind === 'region') {
    return {
      ...project,
      globalCss: captured.globalCss,
      regions: project.regions.map((r) =>
        r.entry.slug === target.slug ? { ...r, html: captured.html } : r,
      ),
    };
  }
  if (target.kind === 'component') {
    return {
      ...project,
      globalCss: captured.globalCss,
      components: project.components.map((c) =>
        c.name === target.slug ? { ...c, template: captured.html } : c,
      ),
    };
  }
  return {
    ...project,
    globalCss: captured.globalCss,
    pages: project.pages.map((p) =>
      p.entry.slug === target.slug ? { ...p, html: captured.html, css: captured.css } : p,
    ),
  };
}
