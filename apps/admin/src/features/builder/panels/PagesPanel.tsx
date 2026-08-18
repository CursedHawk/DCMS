import { makePageEntry, slugFromRoutePath, type PageEntry } from '@dcms/gjs-schema';
import { FileText, Home, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '../../../components/ui/button';
import { Input } from '../../../components/ui/input';
import { cn } from '../../../lib/cn';
import { useVfs } from '../../site-source';
import { pageFiles } from '../project';
import { useBuilder } from '../store';

/**
 * The site's page index. Pages are entries in `site.json` plus a
 * `pages/<slug>.html` / `styles/pages/<slug>.css` pair, so adding or removing
 * one is a manifest edit and a file create/delete — which is why this panel
 * works purely on the project model and never touches GrapesJS.
 */
export function PagesPanel() {
  const { t } = useTranslation();
  const project = useBuilder((s) => s.project);
  const activeSlug = useBuilder((s) => s.activeSlug);
  const [adding, setAdding] = useState(false);
  const [newTitle, setNewTitle] = useState('');

  if (!project) {
    return <div className="p-4 text-sm text-muted-foreground">{t('builder.noProject')}</div>;
  }

  const addPage = () => {
    const title = newTitle.trim();
    if (!title) return;
    const routePath = `/${slugFromRoutePath(`/${title}`)}`;
    if (project.manifest.pages.some((p) => p.path === routePath)) {
      toast.error(t('builder.pagePathTaken', { path: routePath }));
      return;
    }
    const entry = makePageEntry(project.manifest, routePath, title, crypto.randomUUID());

    useBuilder.getState().update((current) => ({
      ...current,
      manifest: {
        ...current.manifest,
        pages: [...current.manifest.pages, entry],
        nav: [...current.manifest.nav, { label: title, path: routePath }],
      },
      pages: [...current.pages, { entry, html: '', css: '' }],
    }));
    useBuilder.getState().setActiveSlug(entry.slug);
    setNewTitle('');
    setAdding(false);
  };

  const removePage = (entry: PageEntry) => {
    if (project.manifest.pages.length === 1) {
      toast.error(t('builder.lastPage'));
      return;
    }
    useBuilder.getState().update((current) => ({
      ...current,
      manifest: {
        ...current.manifest,
        pages: current.manifest.pages.filter((p) => p.slug !== entry.slug),
        nav: current.manifest.nav.filter((n) => n.path !== entry.path),
      },
      pages: current.pages.filter((p) => p.entry.slug !== entry.slug),
    }));
    // `update` only diffs the files a project maps to, and a removed page no
    // longer maps to any — delete them explicitly so the draft matches.
    for (const path of pageFiles(entry.slug)) useVfs.getState().deleteFile(path);
  };

  const renamePage = (entry: PageEntry, title: string) => {
    const trimmed = title.trim();
    if (!trimmed || trimmed === entry.title) return;
    useBuilder.getState().update((current) => ({
      ...current,
      manifest: {
        ...current.manifest,
        // The slug is the file name; renaming the title must not move files, so
        // only the display title and SEO title change here.
        pages: current.manifest.pages.map((p) =>
          p.slug === entry.slug ? { ...p, title: trimmed, seo: { ...p.seo, title: trimmed } } : p,
        ),
        nav: current.manifest.nav.map((n) => (n.path === entry.path ? { ...n, label: trimmed } : n)),
      },
      pages: current.pages.map((p) =>
        p.entry.slug === entry.slug
          ? { ...p, entry: { ...p.entry, title: trimmed, seo: { ...p.entry.seo, title: trimmed } } }
          : p,
      ),
    }));
  };

  const setHome = (entry: PageEntry) => {
    useBuilder.getState().updateManifest((manifest) => ({
      ...manifest,
      pages: manifest.pages.map((p) => ({ ...p, home: p.slug === entry.slug })),
    }));
  };

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          {t('builder.pages')}
        </span>
        <Button size="icon" variant="ghost" onClick={() => setAdding((v) => !v)} title={t('builder.addPage')}>
          <Plus className="h-4 w-4" />
        </Button>
      </div>

      {adding && (
        <div className="flex gap-1 border-b p-2">
          <Input
            autoFocus
            value={newTitle}
            placeholder={t('builder.pageTitle')}
            onChange={(e) => setNewTitle(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') addPage();
              if (e.key === 'Escape') setAdding(false);
            }}
          />
          <Button size="sm" onClick={addPage}>
            {t('actions.add')}
          </Button>
        </div>
      )}

      <ul className="min-h-0 flex-1 overflow-auto p-1">
        {project.manifest.pages.map((entry) => (
          <li key={entry.slug}>
            <div
              className={cn(
                'group flex items-center gap-1 rounded-md px-2 py-1.5 text-sm',
                entry.slug === activeSlug ? 'bg-accent text-accent-foreground' : 'hover:bg-accent/50',
              )}
            >
              <button
                type="button"
                className="flex min-w-0 flex-1 items-center gap-2 text-left"
                onClick={() => useBuilder.getState().setActiveSlug(entry.slug)}
                onDoubleClick={() => {
                  const title = prompt(t('builder.pageTitle'), entry.title);
                  if (title !== null) renamePage(entry, title);
                }}
              >
                <FileText className="h-4 w-4 shrink-0 text-muted-foreground" />
                <span className="min-w-0 flex-1 truncate">{entry.title}</span>
                <span className="shrink-0 text-xs text-muted-foreground">{entry.path}</span>
              </button>
              <button
                type="button"
                title={t('builder.setHome')}
                onClick={() => setHome(entry)}
                className={cn(
                  'shrink-0 rounded p-1',
                  entry.home ? 'text-primary' : 'text-muted-foreground opacity-0 group-hover:opacity-100',
                )}
              >
                <Home className="h-3.5 w-3.5" />
              </button>
              <button
                type="button"
                title={t('actions.delete')}
                onClick={() => removePage(entry)}
                className="shrink-0 rounded p-1 text-muted-foreground opacity-0 hover:text-destructive group-hover:opacity-100"
              >
                <Trash2 className="h-3.5 w-3.5" />
              </button>
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}
