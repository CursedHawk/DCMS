import { AlertTriangle } from 'lucide-react';
import { useEffect, useState } from 'react';
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
import { Input } from './ui/input';
import { Label } from './ui/label';

/**
 * Confirmation for deletes that cannot be undone and destroy more than one thing —
 * a site (builds, artifacts, git repo), a tenant, an account.
 *
 * The everyday `window.confirm` used elsewhere in the app is right for a single
 * recoverable row, but it is one keystroke away from destroying work that has no
 * backup. Here the operator has to read the consequences and type the name back,
 * which is the standard guard for this class of action.
 */
export function ConfirmDeleteDialog({
  open,
  onOpenChange,
  title,
  description,
  consequences,
  confirmationValue,
  confirmLabel,
  pending,
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description: string;
  /** Bullet list of what will be destroyed. */
  consequences: string[];
  /** The exact text the operator must type — normally the resource's name. */
  confirmationValue: string;
  confirmLabel: string;
  pending?: boolean;
  onConfirm: () => void;
}) {
  const { t } = useTranslation();
  const [typed, setTyped] = useState('');

  // Reopening for a different resource must not inherit the previous answer,
  // which would leave the button armed before the operator has read anything.
  useEffect(() => {
    if (open) setTyped('');
  }, [open, confirmationValue]);

  const armed = typed.trim() === confirmationValue.trim();

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <AlertTriangle className="h-5 w-5 text-destructive" />
            {title}
          </DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>

        <ul className="list-disc space-y-1 rounded-md border border-destructive/30 bg-destructive/5 px-6 py-3 text-sm">
          {consequences.map((line) => (
            <li key={line}>{line}</li>
          ))}
        </ul>

        <div className="space-y-1.5">
          <Label>{t('common.confirmType', { value: confirmationValue })}</Label>
          <Input
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            placeholder={confirmationValue}
            autoComplete="off"
          />
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            {t('actions.cancel')}
          </Button>
          <Button variant="destructive" disabled={!armed || pending} onClick={onConfirm}>
            {confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
