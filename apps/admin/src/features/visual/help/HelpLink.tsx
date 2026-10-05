import { CircleHelp } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';
import { useVisual } from '../store';
import { articles } from './articles';

/** A "?" beside a panel or a setting: opens the help article about it. */
export function HelpLink({ article, className }: { article: string; className?: string }) {
  const { t, i18n } = useTranslation();
  const title = articles(i18n.language).find((a) => a.id === article)?.title ?? article;
  const label = t('visual.help.about', { title });
  return (
    <button
      type="button"
      onClick={() => useVisual.getState().openHelp(article)}
      aria-label={label}
      title={label}
      className={cn('inline-flex shrink-0 rounded text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring', className)}
    >
      <CircleHelp className="h-3.5 w-3.5" />
    </button>
  );
}
