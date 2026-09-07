import { cn } from '../cn';

/**
 * The bar across the top of the frame.
 *
 * A container rather than a component with a fixed set of controls: the two consoles put
 * genuinely different things here — a tenant switcher and a search trigger in the admin, an
 * "open tenant admin" link and an operator identity in the platform — and encoding both as
 * optional props would produce a component with a dozen of them and no clear shape.
 *
 * `h-14` in the admin, `h-12` in the platform console, from the density tokens.
 */
export function Topbar({
  start,
  end,
  className,
}: {
  start?: React.ReactNode;
  end?: React.ReactNode;
  className?: string;
}) {
  return (
    <header
      className={cn(
        'flex shrink-0 items-center gap-2 border-b bg-background/80 px-3 backdrop-blur sm:px-4',
        'h-[calc(var(--dcms-row-h)+0.75rem)]',
        className,
      )}
    >
      {start}
      <div className="flex-1" />
      <div className="flex items-center gap-0.5 sm:gap-1">{end}</div>
    </header>
  );
}
