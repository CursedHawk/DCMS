import { useRouterState } from '@tanstack/react-router';
import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from '@dcms/ui';
import { isAgentWatched, openAgentInDock, type SiteSession, useAgentHost } from '../agent/hostStore';
import { useAi } from './context';
import { useSharedAssistant } from './session';

/**
 * Toasts for agents nobody is looking at: one that asks a question, and one that finished.
 *
 * <p>Agents work in the background (ADR 0033), so their questions and their endings happen while
 * the person is somewhere else. A question toast stays until it is answered; clicking any of them
 * opens that agent in the dock's Agents tab (the assistant's own, in its chat). An agent already
 * on screen — its editor panel or its open row in the dock — gets none.</p>
 */
export function AgentNotifier() {
  const { t } = useTranslation();
  const { open, setOpen } = useAi();
  const assistant = useSharedAssistant();
  const onAssistantPage = useRouterState({ select: (s) => s.location.pathname.startsWith('/assistant') });
  const assistantInView = open || onAssistantPage;

  // Read inside a store subscription, which must not be re-made on every render.
  const live = useRef({ t, setOpen });
  live.current = { t, setOpen };

  useEffect(
    () =>
      useAgentHost.subscribe((state, prev) => {
        const { t, setOpen } = live.current;
        for (const session of Object.values(state.sessions)) {
          const before = prev.sessions[session.key];
          const name = session.siteName ?? session.key;
          const show = () => {
            openAgentInDock(session.key);
            setOpen(true);
          };
          const askId = `agent-ask:${session.key}`;

          if (session.question && session.question.id !== before?.question?.id && !isAgentWatched(session.key)) {
            toast.warning(t('assistant.notify.asking', { name }), {
              id: askId,
              description: session.task ?? undefined,
              duration: Infinity,
              action: { label: t('assistant.notify.answer'), onClick: show },
            });
          } else if (!session.question && before?.question) {
            toast.dismiss(askId);
          }

          if (before?.running && !session.running && finished(session) && !isAgentWatched(session.key)) {
            const notify =
              session.outcome === 'failed' ? toast.error : session.outcome === 'paused' ? toast.warning : toast.success;
            notify(t(`assistant.notify.${session.outcome}`, { name }), {
              id: `agent-done:${session.key}`,
              description: session.task ?? undefined,
              duration: 15_000,
              action: { label: t('assistant.notify.open'), onClick: show },
            });
          }
        }
      }),
    [],
  );

  // The console assistant: only when neither the dock nor its page is showing it.
  const questionId = assistant.question?.id ?? null;
  const wasRunning = useRef(assistant.running);
  useEffect(() => {
    if (!questionId) {
      toast.dismiss('assistant-ask');
      return;
    }
    if (assistantInView) return;
    toast.warning(t('assistant.notify.assistantAsking'), {
      id: 'assistant-ask',
      duration: Infinity,
      action: { label: t('assistant.notify.answer'), onClick: () => setOpen(true) },
    });
    // Only a new question raises it; opening the dock is not a reason to raise it again.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [questionId]);

  useEffect(() => {
    const was = wasRunning.current;
    wasRunning.current = assistant.running;
    if (!was || assistant.running || assistantInView) return;
    toast.success(t('assistant.notify.assistantDone'), {
      id: 'assistant-done',
      duration: 15_000,
      action: { label: t('assistant.notify.open'), onClick: () => setOpen(true) },
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assistant.running]);

  return null;
}

/** A run that ended by itself. A stop is the person's own doing and needs no telling. */
function finished(session: SiteSession): session is SiteSession & { outcome: 'completed' | 'paused' | 'failed' } {
  return session.outcome === 'completed' || session.outcome === 'paused' || session.outcome === 'failed';
}
