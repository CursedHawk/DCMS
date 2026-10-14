import { ArrowLeft, ArrowRight, RotateCw } from 'lucide-react';
import { useEffect, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';

/**
 * A preview's address bar, as a browser has one: back, forward, reload, and the path the site
 * is on — type another and press Enter to go there. Reload draws the same page again, not the
 * home page.
 */
export function PreviewAddressBar({
  path,
  onGo,
  onReload,
  reloading,
  children,
}: {
  path: string;
  /** A path, or -1 / 1 for back and forward. */
  onGo: (to: string | number) => void;
  onReload: () => void;
  reloading?: boolean;
  /** Right of the address: the preview's sign-in menu. */
  children?: ReactNode;
}) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState(path);
  const [editing, setEditing] = useState(false);
  // Follows the site while nobody is typing in it.
  useEffect(() => {
    if (!editing) setDraft(path);
  }, [path, editing]);

  const button = 'inline-flex h-6 w-6 shrink-0 items-center justify-center rounded hover:bg-accent hover:text-foreground disabled:opacity-50';
  return (
    <div className="flex h-9 shrink-0 items-center gap-1 border-b bg-card px-2 text-xs text-muted-foreground">
      <button type="button" className={button} aria-label={t('previewBar.back')} title={t('previewBar.back')} onClick={() => onGo(-1)}>
        <ArrowLeft className="h-3.5 w-3.5" />
      </button>
      <button type="button" className={button} aria-label={t('previewBar.forward')} title={t('previewBar.forward')} onClick={() => onGo(1)}>
        <ArrowRight className="h-3.5 w-3.5" />
      </button>
      <button
        type="button"
        className={button}
        aria-label={t('previewBar.reload')}
        title={t('previewBar.reload')}
        disabled={reloading}
        onClick={onReload}
      >
        <RotateCw className={cn('h-3.5 w-3.5', reloading && 'animate-spin')} />
      </button>
      <form
        className="min-w-0 flex-1"
        onSubmit={(e) => {
          e.preventDefault();
          const next = draft.trim();
          onGo(next.startsWith('/') ? next : `/${next}`);
          setEditing(false);
          (document.activeElement as HTMLElement | null)?.blur();
        }}
      >
        <input
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onFocus={(e) => {
            setEditing(true);
            e.currentTarget.select();
          }}
          onBlur={() => setEditing(false)}
          onKeyDown={(e) => {
            if (e.key === 'Escape') {
              setDraft(path);
              e.currentTarget.blur();
            }
          }}
          aria-label={t('previewBar.address')}
          spellCheck={false}
          className="h-6 w-full min-w-0 rounded-full border bg-muted/40 px-3 font-mono text-[11px] text-foreground outline-none focus:border-primary focus:bg-background"
        />
      </form>
      {children ? <div className="min-w-0 shrink-0">{children}</div> : null}
    </div>
  );
}
