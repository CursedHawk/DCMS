import { Monitor } from 'lucide-react';
import { useState } from 'react';
import { Button } from '../ui/button';
import { useIsDesktop } from '../hooks/useMediaQuery';

export interface DesktopRequiredLabels {
  title: string;
  description: string;
  /** The escape hatch. Say what it costs, not "OK". */
  continueAnyway: string;
}

/**
 * The gate in front of the two editors, which are desktop tools and say so.
 *
 * <p><b>Why a panel rather than a responsive layout.</b> Everything else in these consoles
 * works on a phone, and that is deliberate — but a code editor is a file tree beside a
 * two-thousand-column buffer beside a live preview, and there is no arrangement of those that
 * is usable at 390 pixels. Squeezing them produces something that technically renders and that
 * nobody can work in, which is worse than being told plainly to come back on a laptop: a
 * broken layout reads as a bug, and a sentence reads as a decision.</p>
 *
 * <p><b>There is still a way through.</b> Someone on a tablet in landscape, or on a laptop
 * with a browser window they have not maximised, is one pixel below the breakpoint and knows
 * their own situation better than this does. Refusing outright would make the product look
 * broken to the person best placed to judge; the escape hatch costs one line and turns a wall
 * into a warning. It lasts for as long as the component is mounted — reopening the editor asks
 * again, because the reason has not gone away.</p>
 */
export function DesktopRequired({
  children,
  labels,
}: {
  children: React.ReactNode;
  labels: DesktopRequiredLabels;
}) {
  const isDesktop = useIsDesktop();
  const [override, setOverride] = useState(false);

  if (isDesktop || override) {
    return <>{children}</>;
  }

  return (
    <div className="flex min-h-[60vh] flex-col items-center justify-center gap-4 px-6 text-center">
      <div className="flex h-12 w-12 items-center justify-center rounded-full bg-accent text-accent-foreground">
        <Monitor className="h-6 w-6" aria-hidden />
      </div>
      <div className="max-w-sm space-y-1">
        <h2 className="text-base font-semibold tracking-tight">{labels.title}</h2>
        <p className="text-sm text-muted-foreground">{labels.description}</p>
      </div>
      <Button variant="outline" size="sm" onClick={() => setOverride(true)}>
        {labels.continueAnyway}
      </Button>
    </div>
  );
}
