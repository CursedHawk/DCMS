import { Sparkles } from 'lucide-react';
import { useAgentHost } from './hostStore';

/**
 * The site agent's rail icon, saying whether that site's agent is at work.
 *
 * <p>The run outlives the panel (ADR 0033), so the rail is where somebody back from another page
 * sees that it is still going — a ring while it works, a dot when it is waiting for them.</p>
 */
export function AgentRailIcon({ siteId }: { siteId: string }) {
  const running = useAgentHost((state) => state.sessions[siteId]?.running === true);
  const waiting = useAgentHost((state) => state.sessions[siteId]?.pending != null);
  return (
    <span className="relative inline-flex">
      <Sparkles className="h-5 w-5" />
      {running ? (
        <span
          aria-hidden
          className="pointer-events-none absolute -inset-1.5 animate-spin rounded-full border-2 border-primary/70 border-t-transparent motion-reduce:animate-none"
        />
      ) : null}
      {waiting ? (
        <span aria-hidden className="absolute -right-1 -top-1 h-2 w-2 rounded-full bg-warning ring-2 ring-card" />
      ) : null}
    </span>
  );
}
