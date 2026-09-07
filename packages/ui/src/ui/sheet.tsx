import * as DialogPrimitive from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { forwardRef } from 'react';
import { cn } from '../cn';

/**
 * A panel anchored to an edge of the viewport.
 *
 * Built on Radix's Dialog rather than a hand-rolled portal, so it inherits the focus trap,
 * the scroll lock, `Escape`, and the `aria-modal` wiring — all of which a navigation drawer
 * needs and all of which are easy to get subtly wrong.
 *
 * Two jobs, deliberately one component: the navigation drawer on a narrow screen, and the
 * detail panel that slides in beside a table. They differ only in side and width.
 */
export const Sheet = DialogPrimitive.Root;
export const SheetTrigger = DialogPrimitive.Trigger;
export const SheetClose = DialogPrimitive.Close;

const sides = {
  left: 'inset-y-0 left-0 h-full border-r dcms-sheet-left',
  right: 'inset-y-0 right-0 h-full border-l dcms-sheet-right',
  bottom: 'inset-x-0 bottom-0 max-h-[85dvh] rounded-t-xl border-t dcms-sheet-bottom',
} as const;

export const SheetContent = forwardRef<
  React.ElementRef<typeof DialogPrimitive.Content>,
  React.ComponentPropsWithoutRef<typeof DialogPrimitive.Content> & {
    side?: keyof typeof sides;
    /** Width for the left/right sides. Ignored by `bottom`, which is full width. */
    width?: string;
    /** Hides the built-in close button, for a sheet whose content supplies its own. */
    hideClose?: boolean;
  }
>(({ className, children, side = 'right', width = 'w-72', hideClose, ...props }, ref) => (
  <DialogPrimitive.Portal>
    <DialogPrimitive.Overlay className="dcms-overlay fixed inset-0 z-50 bg-black/50 backdrop-blur-sm" />
    <DialogPrimitive.Content
      ref={ref}
      className={cn(
        'fixed z-50 flex flex-col bg-card shadow-xl outline-none',
        sides[side],
        side !== 'bottom' && `max-w-[85vw] ${width}`,
        className,
      )}
      {...props}
    >
      {children}
      {hideClose ? null : (
        <DialogPrimitive.Close className="absolute right-3 top-3 rounded-sm opacity-70 transition-opacity hover:opacity-100 focus:outline-none focus:ring-2 focus:ring-ring">
          <X className="h-4 w-4" />
          <span className="sr-only">Close</span>
        </DialogPrimitive.Close>
      )}
    </DialogPrimitive.Content>
  </DialogPrimitive.Portal>
));
SheetContent.displayName = 'SheetContent';

export function SheetHeader({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('flex shrink-0 flex-col gap-1 border-b p-4 pr-10', className)} {...props} />;
}

/** `min-h-0` is what lets this actually scroll; a flex item defaults to min-height:auto. */
export function SheetBody({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('min-h-0 flex-1 overflow-y-auto p-4', className)} {...props} />;
}

export function SheetFooter({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('flex shrink-0 justify-end gap-2 border-t p-4', className)} {...props} />;
}

export const SheetTitle = forwardRef<
  React.ElementRef<typeof DialogPrimitive.Title>,
  React.ComponentPropsWithoutRef<typeof DialogPrimitive.Title>
>(({ className, ...props }, ref) => (
  <DialogPrimitive.Title
    ref={ref}
    className={cn('text-base font-semibold leading-none tracking-tight', className)}
    {...props}
  />
));
SheetTitle.displayName = 'SheetTitle';

export const SheetDescription = forwardRef<
  React.ElementRef<typeof DialogPrimitive.Description>,
  React.ComponentPropsWithoutRef<typeof DialogPrimitive.Description>
>(({ className, ...props }, ref) => (
  <DialogPrimitive.Description
    ref={ref}
    className={cn('text-sm text-muted-foreground', className)}
    {...props}
  />
));
SheetDescription.displayName = 'SheetDescription';
