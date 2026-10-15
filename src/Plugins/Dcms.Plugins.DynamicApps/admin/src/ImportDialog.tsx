import { useEffect, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Button, Input, Label, Progress, Sheet, SheetBody, SheetContent, SheetFooter, SheetHeader, SheetTitle, jobFraction, toast,
  toastApiError,
} from '@dcms/ui';
import { instancePath, useJob, usePluginApi, usePluginT } from '@dcms/plugin-ui';
import { rootKey, type ImportResult, type TableDef } from './api';

/** Past 1,000 rows the server queues the work as a background job and answers with its id (ADR 0024). */
type Queued = { jobId: string };
const isQueued = (r: ImportResult | Queued): r is Queued => 'jobId' in r;

/**
 * Records from a CSV file, into one table: checked in full first (a dry run that writes nothing),
 * then created all at once. The header row names the fields by api or display name.
 *
 * <p>A large file is checked and imported by a background job: the dialog follows its progress
 * while it is open, and the import goes on if it is closed — the jobs ring keeps following it.</p>
 */
export function ImportDialog({ slug, table, onClose }: { slug: string; table: TableDef; onClose: () => void }) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const queryClient = useQueryClient();
  const [csv, setCsv] = useState<string | null>(null);
  const [checked, setChecked] = useState<ImportResult | null>(null);
  // A queued check or import, followed live; `dryRun` says what to do with its result.
  const [queued, setQueued] = useState<{ jobId: string; dryRun: boolean } | null>(null);
  const job = useJob(queued?.jobId);
  const run = (dryRun: boolean) =>
    api.post<ImportResult | Queued>(instancePath(slug, `/_records/${table.apiName}/import?dryRun=${dryRun}`), { csv });

  const imported = async (result: ImportResult) => {
    if (result.errorCount > 0) {
      setChecked(result);
      return;
    }
    toast.success(t('import.done', { count: result.created }));
    await queryClient.invalidateQueries({ queryKey: [rootKey(slug)] });
    onClose();
  };

  const check = useMutation({
    mutationFn: () => run(true),
    onSuccess: (r) => (isQueued(r) ? setQueued({ jobId: r.jobId, dryRun: true }) : setChecked(r)),
    onError: (e) => toastApiError(e, t),
  });
  const save = useMutation({
    mutationFn: () => run(false),
    onSuccess: (r) => (isQueued(r) ? setQueued({ jobId: r.jobId, dryRun: false }) : imported(r)),
    onError: (e) => toastApiError(e, t),
  });

  const running = job.data?.job;
  useEffect(() => {
    if (!queued || !running || !['Succeeded', 'Failed', 'Canceled'].includes(running.status)) return;
    setQueued(null);
    const result = (running as { result?: ImportResult }).result;
    if (running.status !== 'Succeeded' || !result) {
      toast.error(t('import.failed'));
    } else if (queued.dryRun) {
      setChecked(result);
    } else {
      void imported(result);
    }
    // Reacting to the job's end, once; `imported` and `t` are stable enough for that.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [running?.status]);

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
          {queued ? (
            <div className="space-y-1.5">
              <p className="text-sm">{t(queued.dryRun ? 'import.checking' : 'import.importing')}</p>
              <Progress value={running ? (jobFraction(running) ?? 0) : 0} label={t('import.title', { table: table.displayName })} />
            </div>
          ) : null}
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
          <Button variant="outline" disabled={!csv || check.isPending || !!queued} onClick={() => check.mutate()}>{t('import.check')}</Button>
          <Button disabled={!checked || checked.errorCount > 0 || save.isPending || !!queued} onClick={() => save.mutate()}>
            {t('import.run', { count: checked?.rows ?? 0 })}
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}
