import { createContext, useContext, useEffect, useMemo, useState } from 'react';

/**
 * What the reader is currently looking at.
 *
 * <p>The difference between an assistant bolted onto a product and one that belongs to it is
 * whether it knows where you are. "Publish this" is a sentence people say to a colleague
 * standing at the same screen; without page context it is a sentence the assistant has to
 * answer with "publish what?".</p>
 *
 * <p>Deliberately small and deliberately not the data itself. It names the area, gives a
 * sentence of description, and lists the ids of anything selected. Everything else the
 * assistant needs it fetches through a tool — with the caller's own permissions, through the
 * ordinary API — rather than reading a copy the page happened to have in memory.</p>
 */
export interface AiPageContext {
  /** The area of the product: 'media', 'content', 'analytics'. Matches the route. */
  area: string;
  /** One line the model can read: "the media library, filtered to the Press kit folder". */
  summary: string;
  /** Ids of whatever is selected or open, if anything. */
  selection?: readonly string[];
}

interface AiContextValue {
  page: AiPageContext | null;
  setPage: (context: AiPageContext | null) => void;
  open: boolean;
  setOpen: (open: boolean) => void;
}

const AiContext = createContext<AiContextValue | null>(null);

export function AiProvider({ children }: { children: React.ReactNode }) {
  const [page, setPage] = useState<AiPageContext | null>(null);
  const [open, setOpen] = useState(false);

  // ⌘J / Ctrl+J. Deliberately not ⌘K, which is navigation — the two are used in the same
  // breath and stealing the shortcut people already know is how a new feature earns
  // resentment rather than use.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key.toLowerCase() === 'j' && (event.metaKey || event.ctrlKey)) {
        event.preventDefault();
        setOpen((was) => !was);
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);

  const value = useMemo(() => ({ page, setPage, open, setOpen }), [page, open]);
  return <AiContext.Provider value={value}>{children}</AiContext.Provider>;
}

export function useAi(): AiContextValue {
  const context = useContext(AiContext);
  if (!context) throw new Error('useAi must be used within an AiProvider');
  return context;
}

/**
 * Declares what this page is showing.
 *
 * Memoise the object, or the effect re-registers on every render — including every keystroke
 * in a search box on the page.
 */
export function useAiPageContext(context: AiPageContext | null): void {
  const { setPage } = useAi();
  useEffect(() => {
    setPage(context);
    return () => setPage(null);
  }, [context, setPage]);
}
