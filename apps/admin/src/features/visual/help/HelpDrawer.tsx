import { ArrowLeft, Compass, Search } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input, Sheet, SheetBody, SheetContent, SheetHeader, SheetTitle } from '@dcms/ui';
import { useVisual } from '../store';
import { articles, search } from './articles';
import { Markdown } from './Markdown';
import type { MiniTour } from './tours';

/**
 * The help drawer (Mode D v2, U5.4): the articles, searchable, one at a time, each with the task
 * tour that shows it. It sits beside the builder without dimming it, so the reader can do what
 * an article says while reading it.
 */
export function HelpDrawer({ onTour }: { onTour: (which: MiniTour) => void }) {
  const { t, i18n } = useTranslation();
  const help = useVisual((s) => s.help);
  const [query, setQuery] = useState('');
  // A search is for this visit to help; the next starts from every article.
  useEffect(() => {
    if (!help) setQuery('');
  }, [help]);
  const list = useMemo(() => articles(i18n.language), [i18n.language]);
  const article = help?.article ? list.find((a) => a.id === help.article) : undefined;
  const { openHelp, closeHelp } = useVisual.getState();
  const found = search(list, query);

  return (
    <Sheet open={help !== null} onOpenChange={(open) => !open && closeHelp()} modal={false}>
      <SheetContent side="right" width="w-96" aria-describedby={undefined} onInteractOutside={(e) => e.preventDefault()}>
        <SheetHeader>
          {article && (
            <button type="button" onClick={() => openHelp()} className="flex items-center gap-1 self-start text-xs text-muted-foreground hover:text-foreground">
              <ArrowLeft className="h-3.5 w-3.5" /> {t('visual.help.allArticles')}
            </button>
          )}
          <SheetTitle>{article?.title ?? t('visual.help.articles')}</SheetTitle>
        </SheetHeader>
        <SheetBody className="text-sm">
          {article ? (
            <>
              <Markdown text={article.body} />
              {article.tour && (
                <Button
                  size="sm"
                  onClick={() => {
                    closeHelp();
                    onTour(article.tour!);
                  }}
                >
                  <Compass className="h-4 w-4" /> {t('visual.help.showMe')}
                </Button>
              )}
            </>
          ) : (
            <>
              <div className="relative mb-3">
                <Search className="absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" />
                <Input value={query} onChange={(e) => setQuery(e.target.value)} placeholder={t('visual.help.search')} aria-label={t('visual.help.search')} className="pl-8" />
              </div>
              {found.length === 0 && <p className="text-muted-foreground">{t('visual.help.noMatch', { query: query.trim() })}</p>}
              <ul className="space-y-1">
                {found.map((a) => (
                  <li key={a.id}>
                    <button type="button" onClick={() => openHelp(a.id)} className="w-full rounded-md p-2 text-left hover:bg-muted/60">
                      <span className="block font-medium">{a.title}</span>
                      <span className="line-clamp-2 text-xs text-muted-foreground">{a.body.split('\n')[0]!.replace(/[*`]/g, '')}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </>
          )}
        </SheetBody>
      </SheetContent>
    </Sheet>
  );
}
