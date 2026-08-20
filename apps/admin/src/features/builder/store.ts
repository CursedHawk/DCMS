import {
  AGENTS_MD,
  GLOBAL_CSS,
  THEME_CSS,
  componentPath,
  homePage,
  pageCssPath,
  pageHtmlPath,
  regionHtmlPath,
  type SiteManifest,
} from '@dcms/gjs-schema';
import { create } from 'zustand';
import { useVfs } from '../site-source';
import { aiGuideFor, projectFiles, readProject, type Project } from './project';

/**
 * Builder state that is *not* file content: which page is open, which view is
 * showing, and whether the project could be read at all.
 *
 * The files themselves stay in the shared working-draft store (`useVfs`) so the
 * builder, the code view and the Source Control panel all see one set of
 * changes, and the Mode B autosave carries them to the server unchanged.
 */

export type ViewMode = 'design' | 'split' | 'code';

/**
 * What the canvas is holding.
 *
 * A region and a tenant component are edited on the same canvas as a page,
 * because they are the same kind of thing — markup styled by the same global
 * stylesheet — and because a second editor would mean a second undo history, a
 * second set of panels and a second chance for the two to disagree about what a
 * component is. Only the file it reads and writes differs.
 *
 * For a component the slug is the definition's name, and the markup it holds is
 * the definition's `template`, which is why capture has to fold the canvas back
 * into JSON rather than write a file straight out.
 */
export type CanvasKind = 'page' | 'region' | 'component';

export interface CanvasTarget {
  kind: CanvasKind;
  slug: string;
}

interface BuilderState {
  project: Project | null;
  /** Why the file map is not a readable project, if it is not. */
  error: string | null;
  activeSlug: string | null;
  /** Whether `activeSlug` names a page, a shared region or a component. */
  activeKind: CanvasKind;
  view: ViewMode;
  /** Bumped whenever the active page's content changes from outside the canvas. */
  reloadToken: number;
  /**
   * The exact file contents the canvas last wrote.
   *
   * This is how a code-view edit is told apart from the canvas's own autosave:
   * both land in the same working draft, so without a record of what the canvas
   * itself produced, every capture would look like an external change and
   * reload the canvas in a loop.
   */
  capturedFiles: Record<string, string>;

  /** Re-read the project from the working-draft file map. */
  syncFromVfs: () => void;
  /** Record what the canvas just wrote, so it is not mistaken for an edit. */
  noteCapture: (files: Record<string, string>) => void;
  setActiveSlug: (slug: string) => void;
  /** Open a region on the canvas instead of a page, or go back to a page. */
  setActiveTarget: (target: CanvasTarget) => void;
  setView: (view: ViewMode) => void;
  /** Apply a change to the project and write the affected files back. */
  update: (mutate: (project: Project) => Project) => void;
  updateManifest: (mutate: (manifest: SiteManifest) => SiteManifest) => void;
  /** Force the canvas to reload the active page (after an out-of-band edit). */
  requestReload: () => void;

  activePage: () => Project['pages'][number] | null;
  activeRegion: () => Project['regions'][number] | null;
  activeComponent: () => Project['components'][number] | null;
  /** The files the canvas currently reads and writes. */
  activeFiles: () => string[];
}

export const useBuilder = create<BuilderState>((set, get) => ({
  project: null,
  error: null,
  activeSlug: null,
  activeKind: 'page',
  view: 'design',
  reloadToken: 0,
  capturedFiles: {},

  syncFromVfs: () => {
    const files = useVfs.getState().files;
    const { project, error } = readProject(files);
    const current = get().activeSlug;
    const kind = get().activeKind;

    // A region, component or page that was deleted drops the canvas back to the
    // home page rather than leaving it pointed at a file that is gone.
    const stillThere =
      kind === 'region'
        ? (project?.regions.some((r) => r.entry.slug === current) ?? false)
        : kind === 'component'
          ? (project?.components.some((c) => c.name === current) ?? false)
          : (project?.pages.some((p) => p.entry.slug === current) ?? false);
    const activeKind: CanvasKind = stillThere ? kind : 'page';
    const activeSlug = project ? (stillThere ? current : homePage(project.manifest).slug) : null;

    // If the active document's source no longer matches what the canvas last
    // wrote, something else changed it — the code view, a git restore, an AI
    // insert — and the canvas has to re-read it.
    const captured = get().capturedFiles;
    const watched = activeSlug === null ? [] : filesFor({ kind: activeKind, slug: activeSlug });
    const drifted = watched.some(
      (path) => captured[path] !== undefined && files[path] !== captured[path],
    );

    set((s) => ({
      project,
      error,
      activeSlug,
      activeKind,
      reloadToken: drifted ? s.reloadToken + 1 : s.reloadToken,
    }));

    // The authoring contract is generated, which has to mean *every* repo has a
    // current one — including the ones that predate it and the one someone just
    // deleted the file from. Opening the site is enough. `writeFile` is a no-op
    // when the content already matches, so this settles after one pass rather
    // than looping through the sync it triggers.
    if (project) useVfs.getState().writeFile(AGENTS_MD, aiGuideFor(project));
  },

  noteCapture: (files) =>
    set((s) => ({ capturedFiles: { ...s.capturedFiles, ...files } })),

  setActiveSlug: (slug) => set({ activeSlug: slug, activeKind: 'page' }),
  setActiveTarget: ({ kind, slug }) => set({ activeSlug: slug, activeKind: kind }),
  setView: (view) => set({ view }),
  requestReload: () => set((s) => ({ reloadToken: s.reloadToken + 1 })),

  update: (mutate) => {
    const project = get().project;
    if (!project) return;
    const next = mutate(project);
    set({ project: next });
    writeProject(project, next);
  },

  updateManifest: (mutate) =>
    get().update((project) => ({ ...project, manifest: mutate(project.manifest) })),

  activePage: () => {
    const { project, activeSlug, activeKind } = get();
    if (activeKind !== 'page') return null;
    return project?.pages.find((p) => p.entry.slug === activeSlug) ?? null;
  },

  activeRegion: () => {
    const { project, activeSlug, activeKind } = get();
    if (activeKind !== 'region') return null;
    return project?.regions.find((r) => r.entry.slug === activeSlug) ?? null;
  },

  activeComponent: () => {
    const { project, activeSlug, activeKind } = get();
    if (activeKind !== 'component') return null;
    return project?.components.find((c) => c.name === activeSlug) ?? null;
  },

  activeFiles: () => {
    const { activeSlug, activeKind } = get();
    return activeSlug === null ? [] : filesFor({ kind: activeKind, slug: activeSlug });
  },
}));

/**
 * The files one canvas target owns.
 *
 * A region has no stylesheet of its own: it appears on every page that uses its
 * layout, so per-region rules would have to be loaded on every page anyway —
 * which is what `global.css` already is.
 */
function filesFor(target: CanvasTarget): string[] {
  if (target.kind === 'region') return [regionHtmlPath(target.slug), GLOBAL_CSS];
  // A component's markup is a field inside its definition, so the file the
  // canvas watches for outside edits is the whole JSON.
  if (target.kind === 'component') return [componentPath(target.slug), GLOBAL_CSS];
  return [pageHtmlPath(target.slug), pageCssPath(target.slug), GLOBAL_CSS];
}

/**
 * Write only what actually changed into the working draft.
 *
 * Writing every file on every keystroke would mark the whole project dirty and
 * make the Source Control panel claim edits the author never made, so each file
 * is compared before being written and files that disappeared are deleted.
 */
function writeProject(before: Project, after: Project): void {
  const previous = projectFiles(before);
  const next = projectFiles(after);
  const vfs = useVfs.getState();

  for (const [path, content] of Object.entries(next)) {
    if (previous[path] !== content) vfs.writeFile(path, content);
  }
  for (const path of Object.keys(previous)) {
    if (!(path in next)) vfs.deleteFile(path);
  }
}

/**
 * Push a whole project into the draft, including files that are byte-identical.
 * Used once after seeding a starter, where nothing exists server-side yet.
 */
export function writeWholeProject(project: Project): void {
  const vfs = useVfs.getState();
  for (const [path, content] of Object.entries(projectFiles(project))) {
    vfs.writeFile(path, content);
  }
}

/**
 * Files the builder rewrites on every save: the theme stylesheet, derived from
 * site.json, and the AI authoring contract, derived from the catalogue. Both are
 * shown read-only, because an edit here would be silently overwritten.
 */
export function isGeneratedBuilderFile(path: string): boolean {
  return path === THEME_CSS || path === AGENTS_MD;
}
