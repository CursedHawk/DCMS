import { createContext, useCallback, useContext, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from './ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from './ui/dialog';

export interface ConfirmOptions {
  title: string;
  description?: React.ReactNode;
  confirmLabel?: string;
  /** A red confirm button: the action removes something. */
  destructive?: boolean;
}

type Confirm = (options: ConfirmOptions) => Promise<boolean>;

const ConfirmContext = createContext<Confirm | null>(null);

/**
 * `await confirm({...})` in place of `window.confirm`.
 *
 * <p>The browser's own box is unstyled, cannot be translated, blocks the whole tab, and on a
 * phone shows the site's address as its title. It stays the right *shape* — one question, two
 * answers, at the point of the click — so this keeps that shape and draws it as a dialog.
 * Irreversible deletes of more than one thing still use `ConfirmDeleteDialog`, which makes the
 * reader type the name back.</p>
 */
export function ConfirmProvider({ children }: { children: React.ReactNode }) {
  const { t } = useTranslation();
  const [options, setOptions] = useState<ConfirmOptions | null>(null);
  const resolver = useRef<((answer: boolean) => void) | null>(null);

  const confirm = useCallback<Confirm>((next) => {
    // A second question while one is open answers the first "no" rather than leaving it hanging.
    resolver.current?.(false);
    setOptions(next);
    return new Promise<boolean>((resolve) => {
      resolver.current = resolve;
    });
  }, []);

  const answer = (value: boolean) => {
    resolver.current?.(value);
    resolver.current = null;
    setOptions(null);
  };

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      <Dialog open={options !== null} onOpenChange={(open) => (open ? undefined : answer(false))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{options?.title}</DialogTitle>
            {options?.description ? (
              <DialogDescription>{options.description}</DialogDescription>
            ) : null}
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => answer(false)}>
              {t('actions.cancel', { defaultValue: 'Cancel' })}
            </Button>
            <Button
              variant={options?.destructive ? 'destructive' : 'default'}
              onClick={() => answer(true)}
            >
              {options?.confirmLabel ?? t('actions.confirm', { defaultValue: 'Confirm' })}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </ConfirmContext.Provider>
  );
}

/**
 * The confirm function. Outside a provider it falls back to `window.confirm`, so a component
 * rendered in a test or a plugin screen without the shell still asks rather than proceeding.
 */
export function useConfirm(): Confirm {
  const confirm = useContext(ConfirmContext);
  return confirm ?? (async (o) => window.confirm(o.title));
}
