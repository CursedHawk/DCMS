import { Tooltip, TooltipContent, TooltipTrigger } from '../ui/tooltip';
import { useCan } from './context';

/**
 * Explains why a control is unavailable, instead of making it disappear.
 *
 * When the caller holds the permission this renders nothing of its own — the child is returned
 * untouched, so there is no wrapper element in the way of layout or of a Radix `asChild`
 * trigger further up.
 *
 * When they do not, the child is wrapped in a span that carries the tooltip. The span is
 * necessary rather than tidy: a `disabled` button emits no pointer events, so a tooltip
 * attached to the button itself would never open — which is precisely the case where the
 * explanation matters.
 *
 * The caller is still responsible for passing `disabled` to the control. This component
 * deliberately does not clone the child to inject it: cloning guesses at a prop name, and
 * silently does nothing for a control that spells it `aria-disabled` or takes none at all.
 */
export function PermissionTooltip({
  perm,
  reason,
  children,
}: {
  perm: string;
  /** Say what is missing in the user's terms — "Needs the media:write permission". */
  reason: string;
  children: React.ReactNode;
}) {
  const allowed = useCan(perm);
  if (allowed) return <>{children}</>;

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex cursor-not-allowed">{children}</span>
      </TooltipTrigger>
      <TooltipContent>{reason}</TooltipContent>
    </Tooltip>
  );
}
