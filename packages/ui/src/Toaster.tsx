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
