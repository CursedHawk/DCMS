import { Compass } from 'lucide-react';
import { Button } from '../ui/button';
import { Tooltip, TooltipContent, TooltipTrigger } from '../ui/tooltip';
import { useTour } from './TourProvider';

/**
 * Starts the tour for whatever page is open.
 *
 * <p>Renders nothing when the page has declared no steps, rather than sitting there disabled.
 * A permanently dead control in the top bar teaches people to stop looking at that corner, and
 * most pages will not have a tour on the day this ships.</p>
 */
export function TourButton() {
  const { steps, start, labels } = useTour();
  if (steps.length === 0) return null;

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button variant="ghost" size="icon" aria-label={labels.start} onClick={start}>
          <Compass className="h-4 w-4" aria-hidden />
        </Button>
      </TooltipTrigger>
      <TooltipContent>{labels.start}</TooltipContent>
    </Tooltip>
  );
}
