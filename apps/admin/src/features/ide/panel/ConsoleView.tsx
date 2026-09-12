import { EyeOff, Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';
import {
  clearPreviewMessages,
  previewAttached,
  previewMessages,
  type PreviewMessage,
} from '../preview/previewBridge';
import { PanelEmpty, PanelUnchecked } from './chrome';
import { clockTime } from './clockTime';

/**
 * What the running page said.
 *
 * <p>The bridge that collects these was built for the agent — so it could tell whether a page
 * that compiles actually works — and the author could not see any of it. That is backwards: the
 * person is the one who can fix a runtime error, and until now their only evidence was the
 * browser's own devtools console, which the preview iframe does not surface separately.</p>
 *
 * <p><b>Polled, not pushed, and deliberately.</b> The bridge is a plain module with no
 * subscription, and the messages it collects arrive from an iframe on its own schedule. A
 * one-second tick while this tab is visible is a hundred times cheaper than the SignalR-shaped
 * machinery it would take to push them, and nothing here is worth a millisecond of latency.
 * Nothing polls while the tab is closed.</p>
 */
export function ConsoleView({ previewEnabled }: { previewEnabled: boolean }) {
  const { t } = useTranslation();
  const [messages, setMessages] = useState<readonly PreviewMessage[]>(() => previewMessages());

  useEffect(() => {
    const tick = () => setMessages(previewMessages());
    tick();
    const timer = setInterval(tick, 1000);
    return () => clearInterval(timer);
  }, []);

  if (!previewEnabled || !previewAttached()) {
    return (
      <PanelUnchecked
        icon={EyeOff}
        title={t('ide.panel.console.off')}
        description={t('ide.panel.console.offHint')}
      />
    );
  }

  if (messages.length === 0) {
    return <PanelEmpty title={t('ide.panel.console.empty')} />;
  }

  return (
    <div className="flex h-full flex-col">
      <div className="flex shrink-0 justify-end px-2 py-1">
        <button
          type="button"
          onClick={() => {
            clearPreviewMessages();
            setMessages([]);
          }}
          className="flex items-center gap-1 rounded px-1.5 py-0.5 text-[11px] text-muted-foreground hover:bg-accent hover:text-foreground"
        >
          <Trash2 className="h-3 w-3" aria-hidden />
          {t('ide.panel.clear')}
        </button>
      </div>
      <ol className="min-h-0 flex-1 overflow-auto px-2 pb-2">
        {messages.map((message, index) => (
          <li
            key={`${message.at}-${index}`}
            className="flex gap-2 py-px font-mono text-[11px] leading-relaxed"
          >
            <span className="shrink-0 tabular-nums text-muted-foreground/70">
              {clockTime(message.at)}
            </span>
            <span className="w-[4.5rem] shrink-0 truncate text-muted-foreground">
              {message.kind}
            </span>
            <span
              className={cn(
                'min-w-0 flex-1 whitespace-pre-wrap break-words',
                message.kind === 'warn' ? 'text-[hsl(var(--warning))]' : 'text-destructive',
              )}
            >
              {message.text}
            </span>
          </li>
        ))}
      </ol>
    </div>
  );
}
