import {
  GLOBAL_CSS,
  SITE_JSON,
  THEME_CSS,
  isGeneratedPath,
  safeParseSiteManifest,
  slugFromPageHtmlPath,
} from '@dcms/gjs-schema';
import { useVfs } from '../../site-source';
import type { Project } from '../project';
import { useBuilder } from '../store';
import type { GeneratedSite, GeneratedSource } from './api';

/**
 * Applying generated source.
 *
 * Everything lands in the working draft, never straight in a commit: an AI change
 * becomes an ordinary uncommitted diff the author can read in the Source Control
 * panel, undo, or throw away. That is the whole safety story — there is no
 * separate "accept" state to get wrong, because the git flow already has one.
 *
 * These write files rather than driving the canvas. The canvas re-reads a page
 * whose file changed underneath it (the store's drift check), so one path covers
 * a generated block, a code-view edit and a git restore alike.
 */

/** Append a generated section to a page, with its rules in the page's stylesheet. */
export function appendBlock(slug: string, source: GeneratedSource): void {
  useBuilder.getState().update((project) => {
    const page = project.pages.find((p) => p.entry.slug === slug);
    if (!page) return project;
    return replacePage(project, slug, {
      html: join(page.html, source.html),
      css: join(page.css, source.css),
    });
  });
}

/** Replace a page's body and rules outright. */
export function replacePageSource(slug: string, source: GeneratedSource): void {
  useBuilder.getState().update((project) => {
    const page = project.pages.find((p) => p.entry.slug === slug);
    if (!page) return project;
    // The generated CSS replaces the page sheet rather than appending to it: a
    // regenerated page has no use for the rules of the page it replaced, and
    // leaving them would slowly fill the file with selectors matching nothing.
    return replacePage(project, slug, { html: source.html, css: source.css });
  });
}

function replacePage(
  project: Project,
  slug: string,
  next: { html: string; css: string },
): Project {
  return {
    ...project,
    pages: project.pages.map((p) => (p.entry.slug === slug ? { ...p, ...next } : p)),
  };
}

function join(existing: string, addition: string): string {
  const left = existing.trimEnd();
  const right = addition.trim();
  if (!right) return existing;
  return left ? `${left}\n\n${right}\n` : `${right}\n`;
}

export interface SiteApplyResult {
  files: Record<string, string>;
  /** Paths that were rejected, and why — shown before anything is written. */
  rejected: { path: string; reason: string }[];
}

/**
 * Vet a generated file map before it replaces a site.
 *
 * The model is asked for a specific set of paths; anything else is dropped
 * rather than written. A generated `package.json` or `.github/workflows/*` in a
 * repo that builds and deploys on push is not a harmless stray file, and
 * `styles/theme.css` is regenerated from `site.json` so a hand-written one would
 * silently vanish on the next save.
 */
export function vetSiteFiles(site: GeneratedSite): SiteApplyResult {
  const files: Record<string, string> = {};
  const rejected: { path: string; reason: string }[] = [];

  for (const [rawPath, content] of Object.entries(site.files)) {
    const path = rawPath.replace(/^\/+/, '').trim();
    if (!path || path.includes('..') || path.includes('\\')) {
      rejected.push({ path: rawPath, reason: 'unsafe path' });
      continue;
    }
    if (isGeneratedPath(path)) {
      rejected.push({ path, reason: 'generated from site.json' });
      continue;
    }
    if (!isExpectedSitePath(path)) {
      rejected.push({ path, reason: 'not part of a Mode A site' });
      continue;
    }
    files[path] = content;
  }

  return { files, rejected };
}

function isExpectedSitePath(path: string): boolean {
  return (
    path === SITE_JSON ||
    path === GLOBAL_CSS ||
    path.startsWith('pages/') ||
    path.startsWith('styles/pages/')
  );
}

/**
 * Why a vetted file map still cannot be applied. A manifest that does not parse,
 * or that lists pages with no markup, would leave the builder unable to open the
 * site it just generated — better to say so than to write it.
 */
export function siteProblems(files: Record<string, string>): string[] {
  const problems: string[] = [];

  const raw = files[SITE_JSON];
  if (raw === undefined) return ['site.json is missing.'];

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (e) {
    return [`site.json is not valid JSON: ${(e as Error).message}`];
  }

  const result = safeParseSiteManifest(parsed);
  if (!result.success) {
    const first = result.error.issues[0];
    const where = first?.path.length ? ` at ${first.path.join('.')}` : '';
    return [`site.json is not a valid site manifest${where}: ${first?.message}`];
  }

  const pageFiles = new Set(
    Object.keys(files)
      .map(slugFromPageHtmlPath)
      .filter((slug): slug is string => slug !== null),
  );
  for (const page of result.data.pages) {
    if (!pageFiles.has(page.slug)) problems.push(`No markup for the page “${page.title}”.`);
  }
  if (!result.data.pages.some((p) => p.path === '/')) {
    problems.push('No page is published at “/”.');
  }

  return problems;
}

/**
 * Replace the whole project.
 *
 * Files the generated site does not mention are deleted, so what is left is the
 * site that was asked for rather than a merge of it with whatever came before —
 * except `styles/theme.css`, which the next save regenerates from `site.json`.
 */
export function applySite(files: Record<string, string>): void {
  const vfs = useVfs.getState();
  const existing = useVfs.getState().files;

  for (const [path, content] of Object.entries(files)) {
    if (existing[path] !== content) vfs.writeFile(path, content);
  }
  for (const path of Object.keys(existing)) {
    if (path === THEME_CSS || path in files) continue;
    if (isOwnedByBuilder(path)) vfs.deleteFile(path);
  }

  // The manifest, and therefore every page, changed — the canvas must re-read
  // rather than keep the page it had open.
  useBuilder.getState().syncFromVfs();
  useBuilder.getState().requestReload();
}

/**
 * Whether a path is the builder's to remove. Committed assets and anything else
 * the author put in the repo by hand survive a regenerate; only the files a Mode
 * A project is made of are replaced.
 */
function isOwnedByBuilder(path: string): boolean {
  return path === SITE_JSON || path === GLOBAL_CSS || path.startsWith('styles/pages/') || slugFromPageHtmlPath(path) !== null;
}

/** What a generated site contains, for the confirmation summary. */
export function summarize(files: Record<string, string>): { pages: number; styles: number } {
  const paths = Object.keys(files);
  return {
    pages: paths.filter((p) => slugFromPageHtmlPath(p) !== null).length,
    styles: paths.filter((p) => p.endsWith('.css')).length,
  };
}
