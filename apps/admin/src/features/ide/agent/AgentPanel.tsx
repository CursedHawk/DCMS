import { Link } from '@tanstack/react-router';
import { Check, RotateCcw, Send, Sparkles, Square, X } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, cn } from '@dcms/admin-ui';
import { useAgentSession } from './useAgentSession';

// The router's typed route union is intentionally loose here (see routes.tsx); a
// string-typed path matches the existing pattern (e.g. sitesPath in IdePage).
const aiSettingsPath: string = '/ai';

// The IDE agent chat panel. Claude runs its tool loop here in the browser, editing
// the live VFS; the user watches files change in their tabs and the preview refresh.
export function AgentPanel({ siteName }: { siteId: string; siteName?: string }) {
  const { t } = useTranslation();
  const session = useAgentSession({ siteName });
  const [input, setInput] = useState('');
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight });
  }, [session.entries, session.pending]);

  const submit = () => {
    if (!input.trim() || session.running) return;
    session.send(input);
    setInput('');
  };

  return (
    <div className="flex h-full flex-col">
      {/* Header */}
      <div className="flex h-9 shrink-0 items-center gap-2 border-b px-3 text-sm font-medium">
        <Sparkles className="h-4 w-4 text-primary" />
        <span className="flex-1">{t('ide.agent.title', 'Assistant')}</span>
        <button
          type="button"
          onClick={() => session.setMode(session.mode === 'auto' ? 'manual' : 'auto')}
          title={t('ide.agent.modeHint', 'Auto applies edits automatically; Manual asks first')}
          className={cn(
            'rounded px-1.5 py-0.5 text-[11px] font-medium',
            session.mode === 'auto'
              ? 'bg-primary/15 text-primary'
              : 'bg-muted text-muted-foreground',
          )}
        >
          {session.mode === 'auto' ? t('ide.agent.auto', 'Auto') : t('ide.agent.manual', 'Manual')}
        </button>
        <button
          type="button"
          onClick={session.reset}
          title={t('common.reset', 'Reset')}
          className="text-muted-foreground hover:text-foreground"
        >
          <RotateCcw className="h-3.5 w-3.5" />
        </button>
      </div>

      {/* Transcript */}
      <div ref={scrollRef} className="min-h-0 flex-1 space-y-2 overflow-y-auto p-3 text-sm">
        {session.entries.length === 0 && (
          <p className="px-1 py-6 text-center text-xs text-muted-foreground">
            {t('ide.agent.empty', 'Ask Claude to build or change your site. It edits your files live.')}
          </p>
        )}
        {session.entries.map((e) => {
          if (e.type === 'assistant' && e.text === '') return null;
          if (e.type === 'user') {
            return (
              <div key={e.id} className="ml-6 rounded-md bg-primary/10 px-2.5 py-1.5 text-foreground">
                {e.text}
              </div>
            );
          }
          if (e.type === 'assistant') {
            return (
              <div key={e.id} className="whitespace-pre-wrap px-0.5 text-foreground">
                {e.text}
              </div>
            );
          }
          if (e.type === 'tool') {
            return (
              <div
                key={e.id}
                className={cn(
                  'flex items-center gap-1.5 px-1 font-mono text-[11px]',
                  e.isError ? 'text-destructive' : 'text-muted-foreground',
                )}
              >
                {e.isError ? <X className="h-3 w-3" /> : <Check className="h-3 w-3" />}
                {e.text}
              </div>
            );
          }
          return (
            <div key={e.id} className="px-1 text-xs text-destructive">
              {e.text}
            </div>
          );
        })}

        {/* Manual-mode approval gate */}
        {session.pending && (
          <div className="space-y-2 rounded-md border border-primary/30 bg-primary/5 p-2">
            <p className="text-xs font-medium">{t('ide.agent.approveTitle', 'Apply these changes?')}</p>
            <ul className="space-y-0.5 font-mono text-[11px] text-muted-foreground">
              {session.pending.tools.map((tu) => (
                <li key={tu.id}>
                  {tu.name} {(tu.input?.path as string) ?? ''}
                </li>
              ))}
            </ul>
            <div className="flex gap-2">
              <Button size="sm" onClick={() => session.approve(true)}>
                <Check className="h-3.5 w-3.5" /> {t('common.apply', 'Apply')}
              </Button>
              <Button size="sm" variant="outline" onClick={() => session.approve(false)}>
                <X className="h-3.5 w-3.5" /> {t('common.reject', 'Reject')}
              </Button>
            </div>
          </div>
        )}
      </div>

      {/* Needs-key banner */}
      {session.needsKey && (
        <div className="mx-3 mb-2 rounded-md border border-amber-500/40 bg-amber-500/10 p-2 text-xs">
          <p className="mb-1 font-medium">{t('ide.agent.needsKeyTitle', 'Connect your Anthropic account')}</p>
          <p className="text-muted-foreground">
            {t('ide.agent.needsKeyBody', 'Add your Anthropic API key to use the assistant.')}
          </p>
          <Link to={aiSettingsPath} className="mt-1 inline-block font-medium text-primary hover:underline">
            {t('ide.agent.needsKeyLink', 'Open AI settings →')}
          </Link>
        </div>
      )}

      {/* Composer */}
      <div className="shrink-0 border-t p-2">
        <div className="flex items-end gap-2">
          <textarea
            value={input}
            onChange={(ev) => setInput(ev.target.value)}
            onKeyDown={(ev) => {
              if (ev.key === 'Enter' && !ev.shiftKey) {
                ev.preventDefault();
                submit();
              }
            }}
            rows={2}
            placeholder={t('ide.agent.placeholder', 'Describe a change…')}
            className="min-h-9 flex-1 resize-none rounded-md border bg-background px-2 py-1.5 text-sm outline-none focus:ring-1 focus:ring-primary"
          />
          {session.running ? (
            <Button size="icon" variant="outline" onClick={session.stop} title={t('common.stop', 'Stop')}>
              <Square className="h-4 w-4" />
            </Button>
          ) : (
            <Button size="icon" onClick={submit} disabled={!input.trim()} title={t('common.send', 'Send')}>
              <Send className="h-4 w-4" />
            </Button>
          )}
        </div>
      </div>
    </div>
  );
}
