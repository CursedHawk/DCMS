import { Toaster as Sonner } from 'sonner';
import { useTheme } from './theme';

/** Sonner toaster bound to the active theme. */
export function Toaster() {
  const { resolved } = useTheme();
  return (
    <Sonner
      theme={resolved}
      position="bottom-right"
      richColors
      closeButton
      toastOptions={{
        classNames: {
          toast: 'rounded-md border bg-card text-card-foreground shadow-lg',
        },
      }}
    />
  );
}

/**
 * The toast function bound to the console's one toaster. Plugin screens import it from here
 * rather than from sonner, whose second copy would toast into a toaster nobody renders.
 */
export { toast } from 'sonner';
