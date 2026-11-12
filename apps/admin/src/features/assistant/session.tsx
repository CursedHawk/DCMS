import { createContext, useCallback, useContext, useEffect, useState } from 'react';
import { useAi } from './context';
import { useAssistantSession } from './useAssistantSession';

type Session = ReturnType<typeof useAssistantSession>;

export interface SharedAssistant extends Session {
  /** The conversation on screen, in the dock and on the full page alike. */
  conversationId: string | null;
  /**
   * Show another conversation, or none for a new one. Stops a run in progress first: the run
   * belongs to the conversation being left, and continuing it under another one is how two
   * loops ended up sharing one transcript.
   */
  select: (id: string | null) => void;
}

const SessionContext = createContext<SharedAssistant | null>(null);

/**
 * The console assistant's one session, for the whole tab (ADR 0033).
 *
 * <p>The dock and the full assistant page each used to create their own. "Open full view" then
 * loaded the dock's conversation into a second session while the first was still running it, and
 * leaving the full page orphaned its loop with no way to see or stop it. Both are now views of
 * this one, which lives in the app shell and so keeps working while you go elsewhere.</p>
 *
 * <p>A context of its own rather than part of `useAi`: every page publishes its context through
 * that one, and re-rendering all of them on each streamed token would be the cost.</p>
 */
export function AssistantSessionProvider({ children }: { children: React.ReactNode }) {
  const { page } = useAi();
  const [conversationId, setConversationId] = useState<string | null>(null);
  const session = useAssistantSession(page, { conversationId, onConversationChange: setConversationId });
  const { running, stop } = session;

  const select = useCallback(
    (id: string | null) => {
      if (id !== conversationId && running) stop();
      setConversationId(id);
    },
    [conversationId, running, stop],
  );

  // Closing the tab ends the run (D1); the browser asks first, as for unsaved work.
  useEffect(() => {
    if (!running) return;
    const ask = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', ask);
    return () => window.removeEventListener('beforeunload', ask);
  }, [running]);

  return (
    <SessionContext.Provider value={{ ...session, conversationId, select }}>{children}</SessionContext.Provider>
  );
}

export function useSharedAssistant(): SharedAssistant {
  const context = useContext(SessionContext);
  if (!context) throw new Error('useSharedAssistant must be used within an AssistantSessionProvider');
  return context;
}

/** Whether the assistant is mid-run, for chrome that may render outside the provider. */
export function useAssistantRunning(): boolean {
  return useContext(SessionContext)?.running ?? false;
}
