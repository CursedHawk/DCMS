import { Bot, KeyRound, Send, Square, Wrench } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from '@tanstack/react-router';
import {
  Badge,
  Button,
  Input,
  Sheet,
  SheetBody,
  SheetContent,
  SheetFooter,
  SheetHeader,
  SheetTitle,
  cn,
} from '@dcms/ui';
import { useAi } from './context';
import { useAssistantSession } from './useAssistantSession';

/**
 * The assistant, on every page.
 *
 * <p>A side panel rather than a route, because the point is that it sits <em>beside</em> what
 * you are doing. Sending someone to /ai to ask about the page they just left is the shape that
 * makes an assistant feel bolted on.</p>
 *
 * <p>It knows where the reader is (`useAiPageContext`) and it is given only the tools that
 * reader's permissions allow — so it can never offer to do something the API would refuse.</p>
 */
export function AiDock() {
  const { t } = useTranslation();
  const { open, setOpen, page } = useAi();
  const navigate = useNavigate();
  const session = useAssistantSession(page);
  const [draft, setDraft] = useState('');
  const endRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    endRef.current?.scrollIntoView({ block: 'end' });
  }, [session.entries]);

  const submit = () => {
    session.send(draft);
    setDraft('');
  };

  return (
    <Sheet open={open} onOpenChange={setOpen}>
      <SheetContent side="right" width="w-[26rem]" className="p-0">
        <SheetHeader className="flex-row items-center gap-2">
          <Bot className="h-4 w-4 shrink-0 text-primary" aria-hidden />
          <SheetTitle className="flex-1">{t('assistant.title')}</SheetTitle>
          {session.entries.length > 0 ? (
            <Button variant="ghost" size="sm" onClick={session.reset}>
              {t('assistant.clear')}
            </Button>
          ) : null}
        </SheetHeader>

        <SheetBody className="space-y-3">
          {session.entries.length === 0 ? (
            <Empty
              page={page?.summary}
              toolCount={session.toolCount}
              onPick={(prompt) => session.send(prompt)}
            />
          ) : (
            session.entries.map((entry) => <Entry key={entry.id} entry={entry} />)
          )}

          {session.needsKey ? (
            <div className="rounded-md border border-[hsl(var(--warning)/0.4)] bg-[hsl(var(--warning)/0.1)] p-3 text-sm">
              <p className="flex items-center gap-1.5 font-medium">
                <KeyRound className="h-4 w-4" aria-hidden />
                {t('assistant.noKeyTitle')}
              </p>
              <p className="mt-1 text-muted-foreground">{t('assistant.noKeyBody')}</p>
              <Button
                variant="outline"
                size="sm"
                className="mt-2"
                onClick={() => {
                  setOpen(false);
                  void navigate({ to: '/settings/ai' as string });
                }}
              >
                {t('assistant.connectAccount')}
              </Button>
            </div>
          ) : null}

          <div ref={endRef} />
        </SheetBody>

        <SheetFooter className="flex-col items-stretch gap-2">
          <form
            onSubmit={(e) => {
              e.preventDefault();
              submit();
            }}
            className="flex gap-2"
          >
            <Input
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              placeholder={t('assistant.placeholder')}
              aria-label={t('assistant.placeholder')}
              disabled={session.running}
            />
            {session.running ? (
              <Button
                type="button"
                variant="outline"
                size="icon"
                onClick={session.stop}
                aria-label={t('assistant.stop')}
              >
                <Square className="h-4 w-4" aria-hidden />
              </Button>
            ) : (
              <Button
                type="submit"
                size="icon"
                disabled={!draft.trim()}
                aria-label={t('assistant.send')}
              >
                <Send className="h-4 w-4" aria-hidden />
              </Button>
            )}
          </form>
          {/* Says what it can reach, in the operator's terms. An assistant whose limits are
              invisible gets asked for things it will never manage, once each. */}
          <p className="text-xs text-muted-foreground">
            {t('assistant.toolCount', { count: session.toolCount })}
          </p>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

function Entry({ entry }: { entry: { type: string; text: string } }) {
  if (entry.type === 'tool') {
    return (
      <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <Wrench className="h-3 w-3 shrink-0" aria-hidden />
        <code>{entry.text}</code>
      </p>
    );
  }
  return (
    <div
      className={cn(
        'rounded-lg px-3 py-2 text-sm',
        entry.type === 'user' && 'ml-6 bg-accent text-accent-foreground',
        entry.type === 'assistant' && 'mr-6 border bg-card',
        entry.type === 'error' && 'border border-destructive/40 bg-destructive/10 text-destructive',
      )}
    >
      <p className="whitespace-pre-wrap">{entry.text}</p>
    </div>
  );
}

function Empty({
  page,
  toolCount,
  onPick,
}: {
  page?: string;
  toolCount: number;
  onPick: (prompt: string) => void;
}) {
  const { t } = useTranslation();
  // Suggestions rather than a blank box: nobody's first thought is what to ask a new
  // assistant, and an empty panel is where most of them are abandoned.
  const suggestions = [
    t('assistant.suggestTraffic'),
    t('assistant.suggestContent'),
    t('assistant.suggestPlugins'),
  ];

  return (
    <div className="space-y-3 py-4 text-sm">
      <p className="text-muted-foreground">
        {page ? t('assistant.hereContext', { page }) : t('assistant.hereGeneric')}
      </p>
      {toolCount === 0 ? (
        <Badge tone="warning">{t('assistant.noTools')}</Badge>
      ) : (
        <ul className="space-y-1.5">
          {suggestions.map((prompt) => (
            <li key={prompt}>
              <button
                type="button"
                onClick={() => onPick(prompt)}
                className="w-full rounded-md border px-3 py-2 text-left text-sm text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
              >
                {prompt}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
