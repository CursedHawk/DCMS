import { Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@dcms/ui';
import { usePluginT } from '@dcms/plugin-ui';

/**
 * A plain yes/no. What this plugin asks about — removing something from a draft, discarding a
 * draft, rolling back — is recoverable, so the console's type-the-name dialog for irreversible
 * deletes would be the wrong weight.
 */
export function ConfirmDialog({ open, onOpenChange, title, description, confirmLabel, pending, onConfirm }: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description: string;
  confirmLabel: string;
  pending?: boolean;
  onConfirm: () => void;
}) {
  const { t } = usePluginT();
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>{t('actions.cancel')}</Button>
          <Button variant="destructive" disabled={pending} onClick={onConfirm}>{confirmLabel}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
