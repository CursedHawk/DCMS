import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, Input, Label, Sheet, SheetBody, SheetContent, SheetFooter, SheetHeader, SheetTitle, toast, toastApiError } from '@dcms/ui';
import { instancePath, usePluginApi, usePluginT } from '@dcms/plugin-ui';
import { rootKey, type ImportResult, type TableDef } from './api';

/**
 * Records from a CSV file, into one table: checked in full first (a dry run that writes nothing),
 * then created all at once. The header row names the fields by api or display name.
 */
export function ImportDialog({ slug, table, onClose }: { slug: string; table: TableDef; onClose: () => void }) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const queryClient = useQueryClient();
  const [csv, setCsv] = useState<string | null>(null);
  const [checked, setChecked] = useState<ImportResult | null>(null);
  const run = (dryRun: boolean) =>
    api.post<ImportResult>(instancePath(slug, `/_records/${table.apiName}/import?dryRun=${dryRun}`), { csv });
  const check = useMutation({ mutationFn: () => run(true), onSuccess: setChecked, onError: (e) => toastApiError(e, t) });
  const save = useMutation({
    mutationFn: () => run(false),
    onSuccess: async (result) => {
      if (result.errorCount > 0) {
        setChecked(result);
        return;
      }
      toast.success(t('import.done', { count: result.created }));
      await queryClient.invalidateQueries({ queryKey: [rootKey(slug)] });
      onClose();
    },
    onError: (e) => toastApiError(e, t),
  });

  return (
    <Sheet open onOpenChange={(open) => !open && onClose()}>
      <SheetContent className="sm:max-w-xl">
        <SheetHeader><SheetTitle>{t('import.title', { table: table.pluralName ?? table.displayName })}</SheetTitle></SheetHeader>
        <SheetBody className="space-y-4">
          <p className="text-sm text-muted-foreground">{t('import.hint', { fields: table.fields.map((f) => f.apiName).join(', ') })}</p>
          <div className="space-y-1">
            <Label htmlFor="import-file">{t('import.file')}</Label>
            <Input id="import-file" type="file" accept=".csv,text/csv"
              onChange={async (e) => {
                setChecked(null);
                const file = e.target.files?.[0];
                setCsv(file ? await file.text() : null);
              }} />
          </div>
          {checked ? (
            checked.errorCount === 0 ? (
              <p className="text-sm">{t('import.ready', { count: checked.rows })}</p>
            ) : (
              <div className="space-y-1">
                <p className="text-sm text-destructive">{t('import.problems', { count: checked.errorCount, rows: checked.rows })}</p>
                <ul className="max-h-64 overflow-y-auto rounded-md border p-2 text-xs">
                  {checked.errors.map((e, i) => (
                    <li key={i}>{t('import.row', { row: e.row })} · <code>{e.field}</code>: {e.message}</li>
                  ))}
                </ul>
              </div>
            )
          ) : null}
        </SheetBody>
        <SheetFooter>
          <Button variant="outline" onClick={onClose}>{t('actions.cancel')}</Button>
          <Button variant="outline" disabled={!csv || check.isPending} onClick={() => check.mutate()}>{t('import.check')}</Button>
          <Button disabled={!checked || checked.errorCount > 0 || save.isPending} onClick={() => save.mutate()}>
            {t('import.run', { count: checked?.rows ?? 0 })}
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}
