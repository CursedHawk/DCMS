import type { DcmsComponentSpec } from '@dcms/gjs-schema';
import { GLOBAL_CSS, SITE_JSON, THEME_CSS, pageCssPath, pageHtmlPath } from '@dcms/gjs-schema';
import { FileCode2, FileJson, Palette } from 'lucide-react';
import { useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '../../../lib/cn';
import { MonacoEditor, monaco, setGeneratedPathPredicate, setupMonaco, useVfs } from '../../site-source';
import { isGeneratedBuilderFile, useBuilder } from '../store';
import { registerHtmlData } from './htmlData';
import { registerSiteJsonSchema } from './siteJsonSchema';
import { useIntellisense } from './useIntellisense';

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
  // them: this page's markup, this page's styles, then the shared ones.
  const files = useMemo(() => {
    if (!project || !activeSlug) return [];
    return [
      { path: pageHtmlPath(activeSlug), label: t('builder.fileMarkup'), icon: FileCode2 },
      { path: pageCssPath(activeSlug), label: t('builder.filePageCss'), icon: Palette },
      { path: GLOBAL_CSS, label: t('builder.fileGlobalCss'), icon: Palette },
      { path: THEME_CSS, label: t('builder.fileTheme'), icon: Palette, generated: true },
      { path: SITE_JSON, label: t('builder.fileManifest'), icon: FileJson },
    ];
  }, [project, activeSlug, t]);

  // Open this page's markup by default, and follow the active page.
  useEffect(() => {
    if (!activeSlug) return;
    const target = pageHtmlPath(activeSlug);
    const { files: vfsFiles, activePath: current } = useVfs.getState();
    if (vfsFiles[target] === undefined) return;
    // Only redirect when the open file belongs to a different page; a manual
    // switch to global.css must survive a page change.
    const belongsToAnotherPage = current !== null && /^(pages|styles\/pages)\//.test(current) && current !== target;
    if (current === null || belongsToAnotherPage) useVfs.getState().open(target);
  }, [activeSlug]);

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
