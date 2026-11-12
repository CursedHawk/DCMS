import { Bot } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@dcms/ui';
import { useAgentActivity } from '../agent/hostStore';
import { useAi } from './context';
import { useAssistantRunning } from './session';

/**
 * The assistant's button, which also says when agents are at work (ADR 0033).
 *
 * <p>Site agents and the assistant itself keep running when you leave the page that started
 * them. This is where that stays visible from anywhere in the console: a ring while any of them
 * works, a dot when one is waiting for a decision. The dock's Agents tab says which.</p>
 */
export function AssistantButton() {
  const { t } = useTranslation();
  const ai = useAi();
  const assistantRunning = useAssistantRunning();
  const { working, waiting } = useAgentActivity();
  const busy = assistantRunning || working > 0;

  const label = waiting
    ? t('assistant.agents.buttonWaiting')
    : working > 0
      ? t('assistant.agents.buttonWorking', { count: working })
      : t('assistant.open');

  return (
    <Button variant="ghost" size="icon" aria-label={label} title={label} onClick={() => ai.setOpen(true)}>
      <span className="relative inline-flex">
        <Bot className="h-4 w-4" aria-hidden />
        {busy ? (
          <span
            aria-hidden
            className="pointer-events-none absolute -inset-1.5 animate-spin rounded-full border-2 border-primary/70 border-t-transparent motion-reduce:animate-none"
          />
        ) : null}
        {waiting ? (
          <span aria-hidden className="absolute -right-1 -top-1 h-2 w-2 rounded-full bg-warning ring-2 ring-background" />
        ) : null}
      </span>
    </Button>
  );
}
