import type { DcmsComponentSpec } from '@dcms/gjs-schema';
import {
  GLOBAL_CSS,
  SITE_JSON,
  THEME_CSS,
  componentPath,
  pageCssPath,
  pageHtmlPath,
  regionHtmlPath,
} from '@dcms/gjs-schema';
import { FileCode2, FileJson, Palette } from 'lucide-react';
import { useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '../../../lib/cn';
import { MonacoEditor, monaco, setGeneratedPathPredicate, setupMonaco, useVfs } from '../../site-source';
import { isGeneratedBuilderFile, useBuilder } from '../store';
import { registerHtmlData } from './htmlData';
import { registerSiteJsonSchema } from './siteJsonSchema';
import { useIntellisense } from './useIntellisense';

/** The file that holds the markup of one canvas target. */
function docPath(kind: 'page' | 'region' | 'component', slug: string): string {
  if (kind === 'region') return regionHtmlPath(slug);
  if (kind === 'component') return componentPath(slug);
  return pageHtmlPath(slug);
}

interface CodeFile {
  path: string;
  label: string;
  icon: typeof FileCode2;
  /** Derived from site.json; shown read-only so an edit cannot be lost. */
  generated?: boolean;
}

/**
 * The code view over the project's real files.
 *
 * It is the same Monaco host the Mode B IDE uses, over the same working-draft
 * store — so an edit here goes through exactly the path a canvas edit does, and
 * the two are never two representations of one page that can disagree. The
 * canvas re-reads whatever is typed here after a short pause (see the store's
 * drift check), which is what makes the two views genuinely bidirectional
 * rather than one being a read-only preview of the other.
 */
export function CodeView({ specs }: { specs: readonly DcmsComponentSpec[] }) {
  const { t } = useTranslation();
  const project = useBuilder((s) => s.project);
  const activeSlug = useBuilder((s) => s.activeSlug);
  const activeKind = useBuilder((s) => s.activeKind);
  const activePath = useVfs((s) => s.activePath);

  // Language intelligence: component-aware HTML completion in the html worker,
  // manifest validation in the json worker. Both off the main thread.
  useEffect(() => {
    setupMonaco();
    setGeneratedPathPredicate(isGeneratedBuilderFile);
    registerSiteJsonSchema(monaco);
  }, []);

  useEffect(() => {
    if (specs.length === 0) return;
    setupMonaco();
    registerHtmlData(monaco, specs);
  }, [specs]);

  // What the stock workers cannot know: this project's classes and theme
  // variables, and diagnostics that need every file at once.
  useIntellisense(specs);

  // The files worth switching between, in the order an author thinks about
  // them: this document's markup, its styles, then the shared ones.
  const files = useMemo<CodeFile[]>(() => {
    if (!project || !activeSlug) return [];
    const shared: CodeFile[] = [
      { path: GLOBAL_CSS, label: t('builder.fileGlobalCss'), icon: Palette },
      { path: THEME_CSS, label: t('builder.fileTheme'), icon: Palette, generated: true },
      { path: SITE_JSON, label: t('builder.fileManifest'), icon: FileJson },
    ];
    // A region is one file: its rules live in global.css, which is already here.
    if (activeKind === 'region') {
      return [
        { path: regionHtmlPath(activeSlug), label: t('builder.fileMarkup'), icon: FileCode2 },
        ...shared,
      ];
    }
    // A component's markup lives inside its definition, so the file to edit is
    // the JSON — which is also where its props and its data source are.
    if (activeKind === 'component') {
      return [
        { path: componentPath(activeSlug), label: t('builder.fileComponent'), icon: FileJson },
        ...shared,
      ];
    }
    return [
      { path: pageHtmlPath(activeSlug), label: t('builder.fileMarkup'), icon: FileCode2 },
      { path: pageCssPath(activeSlug), label: t('builder.filePageCss'), icon: Palette },
      ...shared,
    ];
  }, [project, activeSlug, activeKind, t]);

  // Open the active document's markup by default, and follow the canvas.
  useEffect(() => {
    if (!activeSlug) return;
    const target = docPath(activeKind, activeSlug);
    const { files: vfsFiles, activePath: current } = useVfs.getState();
    if (vfsFiles[target] === undefined) return;
    // Only redirect when the open file belongs to a different document; a manual
    // switch to global.css must survive a page change.
    const belongsToAnother =
      current !== null && /^(pages|regions|styles\/pages)\//.test(current) && current !== target;
    if (current === null || belongsToAnother) useVfs.getState().open(target);
  }, [activeSlug, activeKind]);

  if (!project) {
    return (
      <div className="flex h-full items-center justify-center p-6 text-sm text-muted-foreground">
        {t('builder.noProject')}
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col">
      <div className="flex shrink-0 items-center gap-1 overflow-x-auto border-b px-2 py-1.5">
        {files.map(({ path, label, icon: Icon, generated }) => (
          <button
            key={path}
            type="button"
            onClick={() => useVfs.getState().open(path)}
            title={generated ? t('builder.generatedFile') : path}
            className={cn(
              'flex shrink-0 items-center gap-1.5 rounded-md px-2 py-1 text-xs',
              activePath === path ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:bg-accent/50',
            )}
          >
            <Icon className="h-3.5 w-3.5" />
            {label}
            {generated && <span className="text-[10px] opacity-60">{t('builder.readOnly')}</span>}
          </button>
        ))}
      </div>

      {activePath && isGeneratedBuilderFile(activePath) && (
        <p className="shrink-0 border-b bg-amber-500/10 px-3 py-1.5 text-xs text-amber-700 dark:text-amber-400">
          {t('builder.generatedFileHint')}
        </p>
      )}

      <div className="min-h-0 flex-1">
        <MonacoEditor />
      </div>
    </div>
  );
}
